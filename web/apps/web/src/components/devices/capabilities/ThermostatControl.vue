<script setup lang="ts">
import type { ValueSchema } from '@controlhome/api-client'
import { Button, ToggleGroup, ToggleGroupItem, cn } from '@controlhome/ui'
import { Droplets, Fan, Flame, Gauge, Minus, Plus, Power, Snowflake } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, useId, type Component } from 'vue'
import { useCapabilityState } from '../../../composables/useStateValue'
import { allowedValues, numberBounds, snapNumber } from '../../schema/schema'
import { measurement } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `thermostat`: a 270° dial for the target temperature, the room temperature as a tick on it (named "Now" in the
 * middle), the modes and the fan speed as one-row segmented choices. The target's range follows the endpoint's
 * constraints for the current mode (an AC that heats only to 26 °C).
 *
 * One power model: with a `switch` on the endpoint, power is the card's header toggle and "Off" is not a mode.
 *
 * The dial on touch screens: only the ring is a control. A finger elsewhere (the reading in the middle, the corners)
 * scrolls the page; on the ring it scrolls too when it moves up or down first, and sets the target when it slides
 * along or taps; the knob drags at once. − / + and the arrow keys step it.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, locale } = control
const id = useId()

const TARGET: Extract<ValueSchema, { type: 'number' }> = {
  type: 'number',
  min: 5,
  max: 35,
  step: 0.5,
  unit: '°C',
}
const MODES = ['off', 'cool', 'heat', 'auto', 'dry', 'fan']
const FAN_MODES = ['auto', 'quiet', 'low', 'medium', 'high', 'turbo']
const MODE_ICONS: Record<string, Component> = {
  off: Power,
  cool: Snowflake,
  heat: Flame,
  auto: Gauge,
  dry: Droplets,
  fan: Fan,
}
/** Each mode's color, on the dial's arc and on its selected segment (spelled out for Tailwind). */
const MODE_TONES: Record<string, { arc: string; segment: string }> = {
  cool: {
    arc: 'stroke-state-info',
    segment:
      'data-[state=on]:border-state-info data-[state=on]:bg-state-info/15 data-[state=on]:text-foreground',
  },
  dry: {
    arc: 'stroke-state-info',
    segment:
      'data-[state=on]:border-state-info data-[state=on]:bg-state-info/15 data-[state=on]:text-foreground',
  },
  heat: {
    arc: 'stroke-state-warn',
    segment:
      'data-[state=on]:border-state-warn data-[state=on]:bg-state-warn/15 data-[state=on]:text-foreground',
  },
  auto: {
    arc: 'stroke-state-on',
    segment: 'data-[state=on]:border-state-on data-[state=on]:bg-state-on/15 data-[state=on]:text-foreground',
  },
  fan: {
    arc: 'stroke-muted-foreground',
    segment:
      'data-[state=on]:border-muted-foreground data-[state=on]:bg-muted data-[state=on]:text-foreground',
  },
  off: {
    arc: 'stroke-state-off',
    segment:
      'data-[state=on]:border-muted-foreground data-[state=on]:bg-muted data-[state=on]:text-foreground',
  },
}
const SEGMENT_SELECTED = 'data-[state=on]:border-foreground/50 data-[state=on]:bg-accent'

const schemaOf = (key: string) =>
  control.definition.value?.attributes.find((entry) => entry.key === key)?.schema

const bounds = computed(() => numberBounds(TARGET, control.constraint('targetTemperature')))
const min = computed(() => bounds.value.min ?? 5)
const max = computed(() => bounds.value.max ?? 35)
const step = computed(() => bounds.value.step ?? 0.5)

const mode = computed(() => control.state.string('mode'))
const target = computed(() => control.state.number('targetTemperature'))
const current = computed(() => control.state.number('currentTemperature'))
const fanMode = computed(() => control.state.string('fanMode'))

// An AC's power switch: while it is off, the dial rests. With one, power is the card's toggle, not a mode.
const switchCapability = computed(() =>
  props.endpoint.capabilities.find(
    (capability) => capability.capabilityId === 'switch' && !capability.retired,
  ),
)
const powerSwitch = useCapabilityState(switchCapability)
const running = computed(
  () => powerSwitch.bool('on') !== false && mode.value !== undefined && mode.value !== 'off',
)
const modes = computed(() =>
  allowedValues(schemaOf('mode') ?? { type: 'enum', values: MODES }, control.constraint('mode')).filter(
    (option) => !(switchCapability.value && option === 'off'),
  ),
)
const fanModes = computed(() =>
  control.state.has('fanMode')
    ? allowedValues(schemaOf('fanMode') ?? { type: 'enum', values: FAN_MODES }, control.constraint('fanMode'))
    : [],
)
/** The selected mode segment: the mode's own color while running. */
const segmentClass = (option: string) =>
  running.value ? (MODE_TONES[option]?.segment ?? SEGMENT_SELECTED) : SEGMENT_SELECTED

// --- Target: a draft while it is being changed, sent once the gesture ends --------------------------------------------
const draft = ref<number>()
const shown = computed(() => draft.value ?? target.value)
let commitTimer: ReturnType<typeof setTimeout> | undefined

function snap(value: number): number {
  return snapNumber(value, { ...bounds.value, min: min.value, max: max.value })
}

function commit(): void {
  clearTimeout(commitTimer)
  commitTimer = undefined
  const value = draft.value
  if (value === undefined) return
  draft.value = undefined
  if (value !== target.value) void control.send('setTargetTemperature', { targetTemperature: value })
}

function nudge(direction: 1 | -1): void {
  if (!control.enabled.value) return
  draft.value = snap((shown.value ?? (min.value + max.value) / 2) + direction * step.value)
  clearTimeout(commitTimer)
  commitTimer = setTimeout(commit, 700)
}

onBeforeUnmount(commit)

// --- Dial geometry (viewBox 240 × 240) --------------------------------------------------------------------------------
const VIEW = 240
const CENTER = 120
const RADIUS = 96
const START = 135
const SWEEP = 270
/** How far from the ring (in screen px) a pointer still counts as on it. */
const RING_SLOP_PX = 28
/** How far a finger on the ring must move before it drags (a scroll that starts there stays a scroll). */
const TOUCH_SLOP_PX = 6

function point(angle: number, radius = RADIUS): { x: number; y: number } {
  const radians = (angle * Math.PI) / 180
  return { x: CENTER + radius * Math.cos(radians), y: CENTER + radius * Math.sin(radians) }
}

function arc(from: number, to: number): string {
  const a = point(from)
  const b = point(to)
  return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A ${RADIUS} ${RADIUS} 0 ${to - from > 180 ? 1 : 0} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`
}

const fraction = (value: number) =>
  Math.min(1, Math.max(0, (value - min.value) / (max.value - min.value || 1)))
const angleOf = (value: number) => START + SWEEP * fraction(value)

const trackPath = arc(START, START + SWEEP)
const valuePath = computed(() =>
  shown.value === undefined || fraction(shown.value) < 0.002 ? '' : arc(START, angleOf(shown.value)),
)
const knob = computed(() => (shown.value === undefined ? undefined : point(angleOf(shown.value))))
/** The room temperature: a tick across the ring. */
const tick = computed(() => {
  if (current.value === undefined || current.value < min.value || current.value > max.value) return undefined
  const angle = angleOf(current.value)
  return { from: point(angle, RADIUS - 13), to: point(angle, RADIUS + 13) }
})
const arcTone = computed(() =>
  running.value ? (MODE_TONES[mode.value ?? '']?.arc ?? 'stroke-primary') : 'stroke-state-off',
)

const svg = ref<SVGSVGElement>()
const dragging = ref(false)
/** A finger on the ring that has not moved yet: it may still become a scroll. */
let touch: { id: number; x: number; y: number } | undefined

/** Where a pointer is on the dial, in viewBox units from the centre, and the px per unit. */
function polar(event: PointerEvent): { x: number; y: number; scale: number } | undefined {
  const box = svg.value?.getBoundingClientRect()
  if (!box || box.width === 0) return undefined
  const scale = box.width / VIEW
  return {
    x: (event.clientX - box.left) / scale - CENTER,
    y: (event.clientY - box.top) / scale - CENTER,
    scale,
  }
}

function onRing(event: PointerEvent): boolean {
  const at = polar(event)
  if (!at) return false
  return Math.abs(Math.hypot(at.x, at.y) - RADIUS) * at.scale <= RING_SLOP_PX
}

function valueAt(event: PointerEvent): number | undefined {
  const at = polar(event)
  if (!at) return undefined
  let angle = (Math.atan2(at.y, at.x) * 180) / Math.PI
  if (angle < 0) angle += 360
  let along = (angle - START + 360) % 360
  // In the gap at the bottom: the nearer end.
  if (along > SWEEP) along = along > SWEEP + (360 - SWEEP) / 2 ? 0 : SWEEP
  return snap(min.value + (along / SWEEP) * (max.value - min.value))
}

function startDrag(event: PointerEvent, element: Element): void {
  element.setPointerCapture?.(event.pointerId)
  dragging.value = true
  clearTimeout(commitTimer)
  const value = valueAt(event)
  if (value !== undefined) draft.value = value
}

/** The knob: drags at once (it never scrolls the page). */
function onKnobDown(event: PointerEvent): void {
  if (!control.enabled.value || event.button !== 0) return
  event.preventDefault()
  startDrag(event, event.currentTarget as Element)
}

/** The ring: a mouse sets the target at once; a finger waits to see whether it scrolls. */
function onDialDown(event: PointerEvent): void {
  if (!control.enabled.value || event.button !== 0 || !onRing(event)) return
  if (event.pointerType === 'touch') {
    touch = { id: event.pointerId, x: event.clientX, y: event.clientY }
    return
  }
  startDrag(event, event.currentTarget as Element)
}

function onPointerMove(event: PointerEvent): void {
  if (touch && event.pointerId === touch.id && !dragging.value) {
    if (Math.hypot(event.clientX - touch.x, event.clientY - touch.y) < TOUCH_SLOP_PX) return
    touch = undefined
    startDrag(event, event.currentTarget as Element)
    return
  }
  if (!dragging.value) return
  const value = valueAt(event)
  if (value !== undefined) draft.value = value
}

function onPointerUp(event: PointerEvent): void {
  if (touch && event.pointerId === touch.id) {
    // A tap on the ring sets the target there.
    touch = undefined
    const value = valueAt(event)
    if (value !== undefined) {
      draft.value = value
      commit()
    }
    return
  }
  if (!dragging.value) return
  dragging.value = false
  commit()
}

function onPointerCancel(): void {
  // The browser took the gesture (a scroll): keep what was there.
  touch = undefined
  if (!dragging.value) return
  dragging.value = false
  draft.value = undefined
}

function onKeydown(event: KeyboardEvent): void {
  if (!control.enabled.value) return
  const keys: Record<string, () => void> = {
    ArrowUp: () => nudge(1),
    ArrowRight: () => nudge(1),
    ArrowDown: () => nudge(-1),
    ArrowLeft: () => nudge(-1),
    PageUp: () => {
      draft.value = snap((shown.value ?? min.value) + 1)
      commit()
    },
    PageDown: () => {
      draft.value = snap((shown.value ?? max.value) - 1)
      commit()
    },
    Home: () => {
      draft.value = min.value
      commit()
    },
    End: () => {
      draft.value = max.value
      commit()
    },
  }
  const action = keys[event.key]
  if (!action) return
  event.preventDefault()
  action()
}

const targetParts = computed(() =>
  shown.value === undefined ? undefined : measurement(shown.value, '°C', locale.value),
)
const modeTitle = computed(() =>
  running.value ? t(`device.thermostat.action.${mode.value}`) : t('device.thermostat.action.off'),
)
const nowId = `${id}-now`
</script>

<template>
  <div
    class="grid items-center gap-8 md:grid-cols-[minmax(0,18rem)_minmax(0,1fr)]"
    data-slot="thermostat-control"
  >
    <!-- The dial, and − / + under it -->
    <div class="mx-auto grid w-full max-w-72 justify-items-center gap-2">
      <div class="relative aspect-square w-full">
        <div
          role="slider"
          :tabindex="control.enabled.value ? 0 : -1"
          :aria-label="t('device.thermostat.target', { name: control.name.value })"
          :aria-valuemin="min"
          :aria-valuemax="max"
          :aria-valuenow="shown ?? min"
          :aria-valuetext="targetParts ? `${targetParts.value} ${targetParts.unit}` : t('device.noValue')"
          :aria-describedby="current !== undefined ? nowId : undefined"
          :aria-disabled="!control.enabled.value || undefined"
          class="absolute inset-0 rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-4 focus-visible:ring-offset-card"
          @keydown="onKeydown"
        >
          <svg
            ref="svg"
            :viewBox="`0 0 ${VIEW} ${VIEW}`"
            :class="
              cn(
                'size-full touch-pan-y select-none',
                !control.enabled.value && 'cursor-not-allowed',
                dragging && 'cursor-grabbing',
              )
            "
            aria-hidden="true"
            data-slot="thermostat-dial"
            @pointerdown="onDialDown"
            @pointermove="onPointerMove"
            @pointerup="onPointerUp"
            @pointercancel="onPointerCancel"
          >
            <path
              :d="trackPath"
              :class="cn('fill-none stroke-muted', control.enabled.value && 'cursor-pointer')"
              stroke-width="16"
              stroke-linecap="round"
            />
            <path
              v-if="valuePath"
              :d="valuePath"
              :class="
                cn(
                  'fill-none transition-[stroke] duration-500',
                  arcTone,
                  control.enabled.value && 'cursor-pointer',
                )
              "
              stroke-width="16"
              stroke-linecap="round"
            />
            <!-- The room temperature: a tick across the ring, outlined so it reads on the arc and on the track -->
            <g v-if="tick" data-slot="thermostat-now-tick" class="pointer-events-none">
              <line
                :x1="tick.from.x"
                :y1="tick.from.y"
                :x2="tick.to.x"
                :y2="tick.to.y"
                class="stroke-card"
                stroke-width="6"
                stroke-linecap="round"
              />
              <line
                :x1="tick.from.x"
                :y1="tick.from.y"
                :x2="tick.to.x"
                :y2="tick.to.y"
                class="stroke-foreground"
                stroke-width="2.5"
                stroke-linecap="round"
              />
            </g>
            <circle
              v-if="knob"
              :cx="knob.x"
              :cy="knob.y"
              r="13"
              :class="cn('pointer-events-none fill-card transition-[stroke] duration-500', arcTone)"
              stroke-width="5"
            />
          </svg>
          <!-- The knob's grip: a 44 px target that drags at once, also on touch screens -->
          <span
            v-if="knob && control.enabled.value"
            aria-hidden="true"
            data-slot="thermostat-knob"
            class="absolute size-11 -translate-x-1/2 -translate-y-1/2 cursor-grab touch-none rounded-full active:cursor-grabbing"
            :style="{ left: `${(knob.x / VIEW) * 100}%`, top: `${(knob.y / VIEW) * 100}%` }"
            @pointerdown="onKnobDown"
            @pointermove="onPointerMove"
            @pointerup="onPointerUp"
            @pointercancel="onPointerCancel"
          />
        </div>

        <!-- The reading in the middle (not a control: a finger here scrolls the page) -->
        <div
          :class="
            cn(
              'pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-1 text-center transition-opacity',
              !running && 'opacity-70',
            )
          "
        >
          <span class="eyebrow">{{ modeTitle }}</span>
          <span
            class="flex items-start font-serif leading-none font-medium tracking-tight"
            aria-hidden="true"
          >
            <span class="text-6xl tabular-nums">{{ targetParts?.value ?? t('device.noValue') }}</span>
            <span v-if="targetParts" class="mt-1 text-xl text-muted-foreground">{{ targetParts.unit }}</span>
          </span>
          <span
            v-if="current !== undefined"
            :id="nowId"
            class="flex items-center gap-1.5 text-sm text-muted-foreground tabular-nums"
            data-slot="thermostat-now"
          >
            <span aria-hidden="true" class="inline-block h-3 w-0.5 rounded-full bg-foreground" />
            {{ t('device.thermostat.now', { temperature: control.measure(current, '°C') }) }}
          </span>
        </div>
      </div>

      <!-- In the dial's bottom gap, above the dial's own layer (which would otherwise take their taps) -->
      <div class="relative z-10 -mt-14 flex items-center gap-12">
        <Button
          variant="outline"
          size="icon-lg"
          class="rounded-full bg-card"
          :disabled="!control.enabled.value || (shown !== undefined && shown <= min)"
          :aria-label="t('device.thermostat.lower')"
          @click="nudge(-1)"
        >
          <Minus aria-hidden="true" />
        </Button>
        <Button
          variant="outline"
          size="icon-lg"
          class="rounded-full bg-card"
          :disabled="!control.enabled.value || (shown !== undefined && shown >= max)"
          :aria-label="t('device.thermostat.raise')"
          @click="nudge(1)"
        >
          <Plus aria-hidden="true" />
        </Button>
      </div>
    </div>

    <!-- Mode and fan: one row each (scrolls sideways on narrow screens) -->
    <div class="grid min-w-0 content-center gap-6">
      <div class="grid min-w-0 gap-3">
        <span :id="`${id}-mode`" class="text-sm font-medium">{{ control.attributeLabel('mode') }}</span>
        <ToggleGroup
          type="single"
          variant="outline"
          :spacing="2"
          :model-value="mode"
          :disabled="!control.enabled.value"
          :aria-labelledby="`${id}-mode`"
          class="-mx-1 flex w-auto max-w-none gap-2 overflow-x-auto px-1 py-1"
          data-slot="thermostat-modes"
          @update:model-value="(value) => value && value !== mode && control.send('setMode', { mode: value })"
        >
          <ToggleGroupItem
            v-for="option in modes"
            :key="option"
            :value="option"
            :class="
              cn(
                'h-auto min-h-14 min-w-16 flex-1 shrink-0 flex-col gap-1 rounded-xl px-2 py-2 text-xs',
                segmentClass(option),
              )
            "
          >
            <component :is="MODE_ICONS[option] ?? Gauge" class="size-5" aria-hidden="true" />
            {{ control.valueLabel('mode', option) }}
          </ToggleGroupItem>
        </ToggleGroup>
      </div>

      <div v-if="fanModes.length > 0" class="grid min-w-0 gap-3">
        <span :id="`${id}-fan`" class="flex items-center gap-2 text-sm font-medium">
          <Fan class="size-4 text-muted-foreground" aria-hidden="true" />
          {{ control.attributeLabel('fanMode') }}
        </span>
        <ToggleGroup
          type="single"
          variant="outline"
          :spacing="2"
          :model-value="fanMode"
          :disabled="!control.enabled.value"
          :aria-labelledby="`${id}-fan`"
          class="-mx-1 flex w-auto max-w-none gap-2 overflow-x-auto px-1 py-1"
          data-slot="thermostat-fan"
          @update:model-value="
            (value) => value && value !== fanMode && control.send('setFanMode', { fanMode: value })
          "
        >
          <ToggleGroupItem
            v-for="option in fanModes"
            :key="option"
            :value="option"
            :class="cn('min-w-16 flex-1 shrink-0 rounded-xl px-2 text-xs', SEGMENT_SELECTED)"
          >
            {{ control.valueLabel('fanMode', option) }}
          </ToggleGroupItem>
        </ToggleGroup>
      </div>
    </div>
  </div>
</template>
