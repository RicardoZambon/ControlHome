<script setup lang="ts">
import type { FloorBackgroundDto } from '@controlhome/api-client'
import { Button, Tooltip, TooltipContent, TooltipTrigger, cn } from '@controlhome/ui'
import { Maximize, Minus, Plus } from '@lucide/vue'
import { computed, ref, useId, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import KeyHint from '../KeyHint.vue'
import BackgroundLayer from './BackgroundLayer.vue'
import GridLayer from './GridLayer.vue'
import RoomShape, { type PlanRoom } from './RoomShape.vue'
import { contains, distance, type Point } from './geometry'
import { formatLength } from './measure'
import { scaleBarLength, screenToCm, usePanZoom } from './usePanZoom'

/** What the canvas draws: a floor's size, grid, background and rooms (a `FloorDto`, or the editor's draft). */
export interface FloorPlan {
  name?: string
  widthCm: number
  heightCm: number
  gridCm: number
  background: FloorBackgroundDto | null
  rooms: readonly (PlanRoom & { id?: number | null; key?: string })[]
}

/** A pointer event on the canvas, with its position in container px and in floor cm. */
export interface CanvasPointer {
  cm: Point
  px: Point
  event: PointerEvent | MouseEvent
}

/**
 * Lets an editor tool take pointer input. `down` returns true when the tool takes the gesture (it then gets the moves
 * and the up, and the canvas does not pan); otherwise a drag pans the plan and a tap selects.
 */
export interface CanvasPointerHandler {
  down?: (pointer: CanvasPointer) => boolean
  move?: (pointer: CanvasPointer) => void
  up?: (pointer: CanvasPointer) => void
  cancel?: () => void
  /** A click or tap that no tool took (the plan did not move); true when handled. */
  tap?: (pointer: CanvasPointer) => boolean
  dblclick?: (pointer: CanvasPointer) => void
  leave?: () => void
}

type RoomId = string | number

/**
 * The floor plan: an SVG sheet of `widthCm × heightCm` (viewBox in cm, origin top-left) with the drafting grid, the
 * traced background, the rooms and an `overlay` slot (editor handles now, devices later). Pan by dragging the empty plan,
 * with the middle mouse button or one finger; zoom with the wheel, a pinch or the buttons; "fit" shows the whole floor.
 */
const props = withDefaults(
  defineProps<{
    floor: FloorPlan
    mode?: 'view' | 'edit'
    selectedRoomId?: RoomId | null
    /** Highlighted room (the editor's hover); in view mode the canvas tracks the hovered room itself. */
    hoverRoomId?: RoomId | null
    invalidRoomIds?: readonly RoomId[]
    /** Pan and zoom (off for dashboard previews, which are links). */
    interactive?: boolean
    /** Dashboard-size: smaller labels and padding, no rulers or controls. */
    compact?: boolean
    handler?: CanvasPointerHandler
    /** A drag that no tool takes pans the plan. */
    panOnDrag?: boolean
    /** Every primary drag pans (Space held in the editor). */
    forcePan?: boolean
    /** Rooms are keyboard focusable buttons (`room-click` on Enter). */
    focusableRooms?: boolean
    cursorClass?: string
    class?: HTMLAttributes['class']
  }>(),
  { mode: 'view', interactive: true, panOnDrag: true, focusableRooms: true },
)

const emit = defineEmits<{
  'room-click': [room: FloorPlan['rooms'][number], key: string]
  'background-click': [point: Point]
  /** The background image loaded: its size in pixels. */
  'background-loaded': [size: { widthPx: number; heightPx: number }]
}>()

defineSlots<{
  overlay?: (props: { scale: number }) => unknown
  controls?: () => unknown
}>()

const { t, locale } = useI18n()
const patternId = `floorplan-dots-${useId()}`

const container = ref<HTMLElement | null>(null)
const content = computed(() => ({ x: 0, y: 0, width: props.floor.widthCm, height: props.floor.heightCm }))
// Room for the dimension lines around the sheet (less on phones, where every pixel of plan counts).
const panZoom = usePanZoom({
  container,
  content,
  padding: () => (props.compact ? 10 : (container.value?.clientWidth ?? 1000) < 480 ? 34 : 44),
})
const { viewBox, scale } = panZoom

const keyOf = (room: FloorPlan['rooms'][number]) => room.key ?? String(room.id)
const same = (a: RoomId | null | undefined, b: string) => a !== null && a !== undefined && String(a) === b
const invalid = computed(() => new Set((props.invalidRoomIds ?? []).map(String)))

const px = (value: number) => value / scale.value

// --- Pointer input -------------------------------------------------------------------------------------------------
const pointers = new Map<number, Point>()
type Gesture = 'none' | 'tool' | 'pan' | 'pinch' | 'ignore'
let gesture: Gesture = 'none'
let last: Point | null = null
let downAt: Point | null = null
let moved = false
let pinchLast: [Point, Point] | null = null
const panning = ref(false)
const ownHover = ref<string | null>(null)

const local = (event: MouseEvent) => panZoom.toLocal(event.clientX, event.clientY)
const pointerOf = (event: PointerEvent | MouseEvent): CanvasPointer => {
  const at = local(event)
  return { px: at, cm: screenToCm(panZoom.view.value, panZoom.size.value, at), event }
}

/** The topmost room under a floor point. */
function roomAt(point: Point) {
  for (let i = props.floor.rooms.length - 1; i >= 0; i--) {
    const room = props.floor.rooms[i]!
    if (contains(room.polygon, point)) return room
  }
  return undefined
}

function pair(): [Point, Point] {
  const [a, b] = [...pointers.values()]
  return [a!, b!]
}

function onPointerDown(event: PointerEvent): void {
  if (!props.interactive && !props.handler) return
  ;(event.currentTarget as Element).setPointerCapture?.(event.pointerId)
  pointers.set(event.pointerId, local(event))
  if (pointers.size === 2 && props.interactive) {
    if (gesture === 'tool') props.handler?.cancel?.()
    gesture = 'pinch'
    pinchLast = pair()
    panning.value = false
    return
  }
  if (pointers.size > 2 || gesture === 'ignore') return
  downAt = local(event)
  moved = false
  const middle = event.button === 1
  if (!middle && !props.forcePan && event.button === 0 && props.handler?.down?.(pointerOf(event))) {
    gesture = 'tool'
    return
  }
  if (props.interactive && (middle || props.forcePan || props.panOnDrag)) {
    gesture = 'pan'
    last = downAt
    event.preventDefault()
    return
  }
  gesture = 'none'
}

function onPointerMove(event: PointerEvent): void {
  const at = local(event)
  if (pointers.has(event.pointerId)) pointers.set(event.pointerId, at)
  if (gesture === 'pinch') {
    if (pointers.size >= 2 && pinchLast) {
      const next = pair()
      panZoom.pinch(pinchLast, next)
      pinchLast = next
    }
    return
  }
  if (gesture === 'ignore') return
  if (downAt && distance(downAt, at) > 4) moved = true
  if (gesture === 'pan' && last) {
    if (moved) panning.value = true
    panZoom.pan(at.x - last.x, at.y - last.y)
    last = at
    return
  }
  const pointer = pointerOf(event)
  props.handler?.move?.(pointer)
  if (props.mode === 'view' && !props.handler) {
    const room = event.pointerType === 'mouse' ? roomAt(pointer.cm) : undefined
    ownHover.value = room ? keyOf(room) : null
  }
}

function endGesture(): void {
  gesture = pointers.size > 0 ? 'ignore' : 'none'
  last = null
  downAt = null
  pinchLast = null
  panning.value = false
}

function onPointerUp(event: PointerEvent): void {
  pointers.delete(event.pointerId)
  const current = gesture
  if (current === 'pinch' || current === 'ignore') {
    if (pointers.size === 0) endGesture()
    else gesture = 'ignore'
    return
  }
  if (current === 'tool') {
    props.handler?.up?.(pointerOf(event))
  } else if (!moved && event.button !== 1 && downAt) {
    const pointer = pointerOf(event)
    if (!props.handler?.tap?.(pointer)) {
      const room = roomAt(pointer.cm)
      if (room) emit('room-click', room, keyOf(room))
      else emit('background-click', pointer.cm)
    }
  }
  endGesture()
}

function onPointerCancel(event: PointerEvent): void {
  pointers.delete(event.pointerId)
  if (gesture === 'tool') props.handler?.cancel?.()
  endGesture()
}

function onPointerLeave(): void {
  ownHover.value = null
  if (gesture === 'none') props.handler?.leave?.()
}

function onDblclick(event: MouseEvent): void {
  props.handler?.dblclick?.(pointerOf(event))
}

function onWheel(event: WheelEvent): void {
  if (!props.interactive) return
  event.preventDefault()
  const lines = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 400 : 1
  // Trackpad pinches arrive as ctrl+wheel with small deltas.
  const speed = event.ctrlKey ? 0.01 : 0.0015
  panZoom.zoomBy(Math.exp(-event.deltaY * lines * speed), local(event))
}

const hovered = computed(() => props.hoverRoomId ?? ownHover.value)

// --- Rulers and scale bar ------------------------------------------------------------------------------------------
const widthLabel = computed(() => formatLength(props.floor.widthCm, locale.value))
const heightLabel = computed(() => formatLength(props.floor.heightCm, locale.value))
const barCm = computed(() => scaleBarLength(scale.value))
const barPx = computed(() => barCm.value * scale.value)
const barLabel = computed(() => formatLength(barCm.value, locale.value))

const zoomIn = () => panZoom.zoomBy(1.25)
const zoomOut = () => panZoom.zoomBy(0.8)
const fit = () => panZoom.fit()

/** A phone-size canvas: the zoom buttons go in a row along the bottom, where they cover less of the plan. */
const narrow = computed(() => panZoom.size.value.width > 0 && panZoom.size.value.width < 560)

const zoomControls = computed(() => [
  { label: t('floorplan.zoomIn'), icon: Plus, key: '+', run: zoomIn },
  { label: t('floorplan.zoomOut'), icon: Minus, key: '−', run: zoomOut },
  { label: t('floorplan.fit'), icon: Maximize, key: '0', run: fit },
])

defineExpose({ zoomIn, zoomOut, fit, scale, toCm: panZoom.toCm, isFitted: panZoom.isFitted })

const ariaLabel = computed(() =>
  props.floor.name
    ? t('floorplan.planLabel', { name: props.floor.name, count: props.floor.rooms.length })
    : t('floorplan.planLabelUnnamed', { count: props.floor.rooms.length }),
)
</script>

<template>
  <div
    ref="container"
    :class="cn('relative isolate overflow-hidden bg-muted/50 select-none', props.class)"
    data-slot="floor-plan"
  >
    <svg
      :viewBox="`${viewBox.x} ${viewBox.y} ${viewBox.width} ${viewBox.height}`"
      preserveAspectRatio="xMidYMid meet"
      :role="compact ? undefined : 'group'"
      :aria-label="compact ? undefined : ariaLabel"
      :aria-hidden="compact ? 'true' : undefined"
      :data-scale="scale"
      :class="
        cn(
          'absolute inset-0 size-full',
          interactive ? 'touch-none' : 'pointer-events-none',
          panning ? 'cursor-grabbing' : cursorClass,
        )
      "
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp"
      @pointercancel="onPointerCancel"
      @pointerleave="onPointerLeave"
      @dblclick="onDblclick"
      @wheel="onWheel"
      @contextmenu.prevent
    >
      <defs v-if="!compact">
        <pattern :id="patternId" patternUnits="userSpaceOnUse" :width="px(18)" :height="px(18)">
          <circle :cx="px(9)" :cy="px(9)" :r="px(0.9)" class="fill-muted-foreground/30" />
        </pattern>
      </defs>
      <!-- The drafting table around the sheet -->
      <rect
        v-if="!compact"
        :x="viewBox.x"
        :y="viewBox.y"
        :width="viewBox.width"
        :height="viewBox.height"
        :fill="`url(#${patternId})`"
        class="pointer-events-none"
      />

      <!-- The sheet: the floor itself -->
      <rect
        x="0"
        y="0"
        :width="floor.widthCm"
        :height="floor.heightCm"
        class="pointer-events-none fill-card drop-shadow-md"
      />
      <BackgroundLayer
        v-if="floor.background"
        :background="floor.background"
        @loaded="emit('background-loaded', $event)"
      />
      <GridLayer
        :width-cm="floor.widthCm"
        :height-cm="floor.heightCm"
        :grid-cm="floor.gridCm"
        :scale="scale"
        :minimal="compact"
      />
      <rect
        x="0"
        y="0"
        :width="floor.widthCm"
        :height="floor.heightCm"
        fill="none"
        class="pointer-events-none stroke-foreground/40"
        :stroke-width="px(1.25)"
      />

      <!-- Dimension lines, like on a drawing -->
      <g v-if="!compact" class="pointer-events-none" aria-hidden="true">
        <g class="stroke-muted-foreground/60" :stroke-width="px(1)">
          <path
            :d="`M0 ${-px(22)}H${floor.widthCm}M0 ${-px(28)}V${-px(16)}M${floor.widthCm} ${-px(28)}V${-px(16)}`"
          />
          <path
            :d="`M${-px(22)} 0V${floor.heightCm}M${-px(28)} 0H${-px(16)}M${-px(28)} ${floor.heightCm}H${-px(16)}`"
          />
        </g>
        <text
          :x="floor.widthCm / 2"
          :y="-px(22)"
          text-anchor="middle"
          dominant-baseline="central"
          :font-size="px(11)"
          paint-order="stroke"
          :stroke-width="px(6)"
          class="fill-muted-foreground stroke-muted font-sans tabular-nums"
        >
          {{ widthLabel }}
        </text>
        <text
          :transform="`translate(${-px(22)} ${floor.heightCm / 2}) rotate(-90)`"
          text-anchor="middle"
          dominant-baseline="central"
          :font-size="px(11)"
          paint-order="stroke"
          :stroke-width="px(6)"
          class="fill-muted-foreground stroke-muted font-sans tabular-nums"
        >
          {{ heightLabel }}
        </text>
      </g>

      <g>
        <RoomShape
          v-for="room in floor.rooms"
          :key="keyOf(room)"
          :room="room"
          :room-key="keyOf(room)"
          :scale="scale"
          :selected="same(selectedRoomId, keyOf(room))"
          :hovered="same(hovered, keyOf(room))"
          :invalid="invalid.has(keyOf(room))"
          :focusable="focusableRooms && !compact"
          :compact="compact"
          class="pointer-events-none"
          @activate="emit('room-click', room, keyOf(room))"
        />
      </g>

      <slot name="overlay" :scale="scale" />
    </svg>

    <slot name="controls" />

    <template v-if="!compact">
      <!-- Scale bar -->
      <div
        class="pointer-events-none absolute bottom-3 left-3 flex flex-col items-start gap-1 rounded-md bg-background/80 px-2 py-1.5 text-xs leading-none text-muted-foreground tabular-nums shadow-xs backdrop-blur-sm"
        aria-hidden="true"
      >
        <span>{{ barLabel }}</span>
        <span
          class="block h-1.5 border-x border-b border-muted-foreground"
          :style="{ width: `${barPx}px` }"
        />
      </div>

      <!-- Zoom -->
      <div
        v-if="interactive"
        :class="
          cn(
            'absolute right-3 bottom-3 flex overflow-hidden rounded-lg border bg-background/90 shadow-sm backdrop-blur-sm',
            narrow ? 'flex-row' : 'flex-col',
          )
        "
      >
        <Tooltip v-for="(control, index) in zoomControls" :key="control.label">
          <TooltipTrigger as-child>
            <Button
              variant="ghost"
              size="icon-sm"
              :class="cn('rounded-none', index === 1 && (narrow ? 'border-x' : 'border-y'))"
              :aria-label="control.label"
              @click="control.run"
            >
              <component :is="control.icon" />
            </Button>
          </TooltipTrigger>
          <TooltipContent :side="narrow ? 'top' : 'left'" class="flex items-center gap-2">
            {{ control.label }}
            <KeyHint v-if="mode === 'edit'" :keys="[control.key]" />
          </TooltipContent>
        </Tooltip>
      </div>
    </template>
  </div>
</template>
