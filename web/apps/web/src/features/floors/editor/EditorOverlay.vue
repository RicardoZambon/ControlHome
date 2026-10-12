<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { boundsOf, distance, labelPoint, type Point } from '../../../components/floorplan/geometry'
import { formatArea, formatLength, roomAreaM2 } from '../../../components/floorplan/measure'
import { roomLabelExtent, roomLabelLayout } from '../../../components/floorplan/roomLabel'
import { drawSegmentCrosses } from './tools/drawTool'
import {
  CLOSE_RADIUS_PX,
  HANDLE_RADIUS_PX,
  MIDPOINT_MIN_WALL_PX,
  type EditorPointer,
} from './useEditorPointer'
import { useEditor } from './useEditorState'
import { wallLabels, type Box } from './wallLabels'

/**
 * What the editor draws over the plan (inside the canvas SVG, never hit by the pointer): corner and wall-midpoint
 * handles of the selected room with its wall lengths, the outline being drawn with its rubber band, the snap marker and
 * the calibration ruler. Sizes are screen px divided by `scale`.
 */
const props = defineProps<{ scale: number; pointer: EditorPointer; coarse: boolean }>()
const state = useEditor()
const { locale } = useI18n()

const px = (value: number) => value / props.scale
const handleRadius = computed(() => px(props.coarse ? HANDLE_RADIUS_PX.coarse : HANDLE_RADIUS_PX.fine))
const pointsOf = (polygon: readonly Point[]) => polygon.map((p) => `${p.x},${p.y}`).join(' ')
const length = (cm: number) => formatLength(cm, locale.value)

const selected = computed(() => (state.tool.value === 'select' ? state.selectedRoom.value : null))
const hover = computed(() => props.pointer.hover.value)

/** Midpoint handles of the selected room, on walls long enough on screen to take one. */
const midpoints = computed(() => {
  const polygon = selected.value?.polygon ?? []
  return polygon
    .map((a, index) => ({ index, a, b: polygon[(index + 1) % polygon.length]! }))
    .filter(({ a, b }) => distance(a, b) * props.scale >= MIDPOINT_MIN_WALL_PX)
    .map(({ index, a, b }) => ({ index, mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } }))
})

/** Where the rooms' own labels are (cm), as `RoomShape` draws them: the wall lengths keep clear of them. */
function labelBoxes(): Box[] {
  return state.rooms.value.flatMap((room) => {
    if (room.polygon.length < 3) return []
    const bounds = boundsOf(room.polygon)
    const layout = roomLabelLayout({
      name: room.name,
      widthPx: (bounds.maxX - bounds.minX) * props.scale,
      heightPx: (bounds.maxY - bounds.minY) * props.scale,
    })
    if (!layout) return []
    const at = labelPoint(room.polygon)
    const extent = roomLabelExtent(layout, formatArea(roomAreaM2(room.polygon), locale.value))
    return [
      {
        minX: at.x - px(extent.halfWidth),
        maxX: at.x + px(extent.halfWidth),
        minY: at.y + px(extent.top),
        maxY: at.y + px(extent.bottom),
      },
    ]
  })
}

/**
 * Wall lengths of the selected room as pills: outside each wall unless a neighbour or the floor's edge is there, and
 * never over a room's label.
 */
const walls = computed(() => {
  const room = selected.value
  if (!room) return []
  const { widthCm, heightCm } = state.draft.value.floor
  return wallLabels(room.polygon, {
    others: state.rooms.value.filter((other) => other.key !== room.key).map((other) => other.polygon),
    widthCm,
    heightCm,
    scale: props.scale,
    minWallPx: MIDPOINT_MIN_WALL_PX,
    gapPx: (props.coarse ? HANDLE_RADIUS_PX.coarse : HANDLE_RADIUS_PX.fine) + 5,
    text: length,
    avoid: labelBoxes(),
  })
})

// --- Drawing ----------------------------------------------------------------------------------------------------------
const drawing = computed(() => (state.tool.value === 'draw' ? state.drawing.value.points : []))
const cursor = computed(() => props.pointer.cursor.value)
const rubberCrosses = computed(() => (cursor.value ? drawSegmentCrosses(drawing.value, cursor.value) : false))
/** With three corners the first one closes the outline: a big, gently pulsing target (as big as its hit area). */
const closeRadius = computed(() => (props.coarse ? CLOSE_RADIUS_PX.coarse : CLOSE_RADIUS_PX.fine))
const canClose = computed(() => drawing.value.length >= 3)
const closable = computed(() => {
  const first = drawing.value[0]
  return (
    !!first &&
    !!cursor.value &&
    canClose.value &&
    distance(first, cursor.value) * props.scale <= closeRadius.value
  )
})
const rubberLength = computed(() => {
  const last = drawing.value.at(-1)
  return last && cursor.value ? distance(last, cursor.value) : 0
})
const rectangle = computed(() => props.pointer.rectangle.value)

// --- Calibration ------------------------------------------------------------------------------------------------------
const calibration = computed(() => (state.tool.value === 'calibrate' ? state.calibration.value : null))
const calibrationEnd = computed(() => calibration.value?.b ?? (calibration.value?.a ? cursor.value : null))

const snapMarker = computed(() =>
  (state.tool.value === 'draw' || state.tool.value === 'select') && props.pointer.snapKind.value
    ? props.pointer.snapKind.value
    : null,
)
const snapPoint = computed(() => (state.tool.value === 'draw' ? cursor.value : null))
</script>

<template>
  <g class="pointer-events-none" aria-hidden="true">
    <!-- Selected room: wall lengths, midpoint handles, corner handles -->
    <template v-if="selected">
      <g
        v-for="wall in walls"
        :key="`len-${wall.index}`"
        :transform="`translate(${wall.at.x} ${wall.at.y}) scale(${1 / scale})`"
        :data-wall-label="wall.inside ? 'inside' : 'outside'"
      >
        <rect
          :x="-wall.widthPx / 2"
          :y="-wall.heightPx / 2"
          :width="wall.widthPx"
          :height="wall.heightPx"
          :rx="wall.heightPx / 2"
          class="fill-popover/90 stroke-primary/40"
          stroke-width="1"
        />
        <text
          text-anchor="middle"
          dominant-baseline="central"
          font-size="11.5"
          class="fill-primary font-sans font-medium tabular-nums"
        >
          {{ wall.text }}
        </text>
      </g>
      <circle
        v-for="wall in midpoints"
        :key="`mid-${wall.index}`"
        :cx="wall.mid.x"
        :cy="wall.mid.y"
        :r="handleRadius * 0.7"
        :stroke-width="px(1.5)"
        :class="
          hover.kind === 'midpoint' && hover.index === wall.index
            ? 'fill-primary stroke-card'
            : 'fill-card stroke-primary/60'
        "
      />
      <circle
        v-for="(corner, index) in selected.polygon"
        :key="`corner-${index}`"
        :cx="corner.x"
        :cy="corner.y"
        :r="
          state.selection.value.vertex === index || (hover.kind === 'vertex' && hover.index === index)
            ? handleRadius * 1.3
            : handleRadius
        "
        :stroke-width="px(2)"
        :class="
          state.selection.value.vertex === index ? 'fill-primary stroke-card' : 'fill-card stroke-primary'
        "
      />
    </template>

    <!-- Outline being drawn -->
    <template v-if="drawing.length > 0">
      <polyline
        :points="pointsOf(drawing)"
        fill="none"
        class="stroke-primary"
        :stroke-width="px(2)"
        stroke-linejoin="round"
        stroke-linecap="round"
      />
      <line
        v-if="cursor"
        :x1="drawing.at(-1)!.x"
        :y1="drawing.at(-1)!.y"
        :x2="closable ? drawing[0]!.x : cursor.x"
        :y2="closable ? drawing[0]!.y : cursor.y"
        :class="rubberCrosses ? 'stroke-destructive' : 'stroke-primary'"
        :stroke-width="px(1.5)"
        :stroke-dasharray="`${px(6)} ${px(4)}`"
      />
      <!-- The first corner closes the outline once there are three: a target as big as the area that takes the tap -->
      <g v-if="canClose" :transform="`translate(${drawing[0]!.x} ${drawing[0]!.y})`" data-close-target>
        <circle
          :r="px(closeRadius)"
          :class="
            closable
              ? 'fill-primary/25 stroke-primary'
              : 'fill-primary/10 stroke-primary/60 motion-safe:animate-pulse'
          "
          :stroke-width="px(1.5)"
        />
      </g>
      <circle
        v-for="(corner, index) in drawing"
        :key="`draw-${index}`"
        :cx="corner.x"
        :cy="corner.y"
        :r="index === 0 && canClose ? handleRadius * 1.3 : handleRadius"
        :stroke-width="px(2)"
        :class="index === 0 ? 'fill-primary stroke-card' : 'fill-card stroke-primary'"
      />
      <text
        v-if="cursor && rubberLength * scale >= 24"
        :x="cursor.x"
        :y="cursor.y"
        :dx="px(14)"
        :dy="px(-14)"
        :font-size="px(11)"
        paint-order="stroke"
        :stroke-width="px(4)"
        stroke-linejoin="round"
        class="fill-primary stroke-card font-sans font-medium tabular-nums"
      >
        {{ length(rubberLength) }}
      </text>
    </template>

    <!-- Rectangle being dragged -->
    <polygon
      v-if="rectangle"
      :points="`${rectangle.a.x},${rectangle.a.y} ${rectangle.b.x},${rectangle.a.y} ${rectangle.b.x},${rectangle.b.y} ${rectangle.a.x},${rectangle.b.y}`"
      class="fill-primary/10 stroke-primary"
      :stroke-width="px(1.5)"
      :stroke-dasharray="`${px(6)} ${px(4)}`"
    />

    <!-- Snap marker where the next corner goes -->
    <g v-if="snapPoint && snapMarker" :transform="`translate(${snapPoint.x} ${snapPoint.y})`">
      <rect
        v-if="snapMarker === 'vertex'"
        :x="-px(6)"
        :y="-px(6)"
        :width="px(12)"
        :height="px(12)"
        fill="none"
        class="stroke-primary"
        :stroke-width="px(1.5)"
      />
      <path
        v-else-if="snapMarker === 'edge'"
        :d="`M0 ${-px(7)}L${px(7)} 0L0 ${px(7)}L${-px(7)} 0Z`"
        fill="none"
        class="stroke-primary"
        :stroke-width="px(1.5)"
      />
      <path
        v-else
        :d="`M${-px(8)} 0H${px(8)}M0 ${-px(8)}V${px(8)}`"
        class="stroke-primary"
        :stroke-width="px(1.5)"
      />
    </g>
    <circle
      v-else-if="snapPoint && state.tool.value === 'draw'"
      :cx="snapPoint.x"
      :cy="snapPoint.y"
      :r="px(3)"
      class="fill-primary"
    />

    <!-- Calibration ruler -->
    <template v-if="calibration?.a">
      <line
        v-if="calibrationEnd"
        :x1="calibration.a.x"
        :y1="calibration.a.y"
        :x2="calibrationEnd.x"
        :y2="calibrationEnd.y"
        class="stroke-state-info"
        :stroke-width="px(2)"
        :stroke-dasharray="calibration.b ? undefined : `${px(6)} ${px(4)}`"
      />
      <g
        v-for="(point, index) in [calibration.a, calibration.b].filter((p): p is Point => p !== null)"
        :key="`cal-${index}`"
        :transform="`translate(${point.x} ${point.y})`"
      >
        <circle :r="px(9)" fill="none" class="stroke-state-info" :stroke-width="px(2)" />
        <path
          :d="`M${-px(14)} 0H${px(14)}M0 ${-px(14)}V${px(14)}`"
          class="stroke-state-info"
          :stroke-width="px(1.25)"
        />
      </g>
    </template>
  </g>
</template>
