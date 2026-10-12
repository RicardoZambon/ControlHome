<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { contains, distance, type Point } from '../../../components/floorplan/geometry'
import { formatLength } from '../../../components/floorplan/measure'
import { drawSegmentCrosses } from './tools/drawTool'
import { HANDLE_RADIUS_PX, MIDPOINT_MIN_WALL_PX, type EditorPointer } from './useEditorPointer'
import { useEditor } from './useEditorState'

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

/** Wall lengths of the selected room, where the wall is long enough on screen to carry a label. */
const walls = computed(() => {
  const polygon = selected.value?.polygon ?? []
  return polygon
    .map((a, index) => {
      const b = polygon[(index + 1) % polygon.length]!
      const cm = distance(a, b)
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
      // The label sits just outside the wall, clear of the midpoint handle.
      let normal = { x: (b.y - a.y) / (cm || 1), y: -(b.x - a.x) / (cm || 1) }
      if (contains(polygon, { x: mid.x + normal.x * px(3), y: mid.y + normal.y * px(3) })) {
        normal = { x: -normal.x, y: -normal.y }
      }
      const offset = px(Math.abs(normal.x) > Math.abs(normal.y) ? 22 : 14)
      const label = { x: mid.x + normal.x * offset, y: mid.y + normal.y * offset }
      return { index, cm, mid, label }
    })
    .filter((wall) => wall.cm * props.scale >= MIDPOINT_MIN_WALL_PX)
})

// --- Drawing ----------------------------------------------------------------------------------------------------------
const drawing = computed(() => (state.tool.value === 'draw' ? state.drawing.value.points : []))
const cursor = computed(() => props.pointer.cursor.value)
const rubberCrosses = computed(() => (cursor.value ? drawSegmentCrosses(drawing.value, cursor.value) : false))
const closable = computed(() => {
  const first = drawing.value[0]
  return (
    !!first &&
    !!cursor.value &&
    drawing.value.length >= 3 &&
    distance(first, cursor.value) * props.scale <= (props.coarse ? 22 : 10)
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
      <g v-for="wall in walls" :key="`len-${wall.index}`">
        <text
          :x="wall.label.x"
          :y="wall.label.y"
          text-anchor="middle"
          dominant-baseline="central"
          :font-size="px(10.5)"
          paint-order="stroke"
          :stroke-width="px(4)"
          stroke-linejoin="round"
          class="fill-primary stroke-card font-sans font-medium tabular-nums"
        >
          {{ length(wall.cm) }}
        </text>
      </g>
      <circle
        v-for="wall in walls"
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
      <circle
        v-for="(corner, index) in drawing"
        :key="`draw-${index}`"
        :cx="corner.x"
        :cy="corner.y"
        :r="index === 0 && closable ? handleRadius * 1.6 : handleRadius"
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
