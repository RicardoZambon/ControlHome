<script setup lang="ts">
import { ref } from 'vue'
import PlacedDevice from './PlacedDevice.vue'
import type { Point } from './geometry'
import type { PlanMarker } from './placements'

/**
 * The devices on a floor plan (inside `FloorPlanCanvas`'s overlay slot).
 *
 * - `view`: focusable markers (Enter opens one); taps are found by the page through the canvas handler, so dragging
 *   over a marker still pans the plan.
 * - `edit`: markers can be dragged to a new spot (`move`, clamped to the floor) or tapped (`select`).
 * - `preview`: small dots, nothing interactive.
 */
const props = withDefaults(
  defineProps<{
    markers: readonly PlanMarker[]
    scale: number
    mode?: 'view' | 'edit' | 'preview'
    selectedId?: number | null
    coarse?: boolean
    /** Dimmed and not interactive (the editor while it edits rooms). */
    muted?: boolean
    /** Client px → floor cm (the canvas's `toCm`), for dragging. */
    toCm?: (clientX: number, clientY: number) => Point
    widthCm?: number
    heightCm?: number
  }>(),
  { mode: 'view', selectedId: null, coarse: false, muted: false },
)

const emit = defineEmits<{
  activate: [marker: PlanMarker]
  select: [marker: PlanMarker]
  move: [marker: PlanMarker, point: Point]
}>()

interface Drag {
  id: number
  pointerId: number
  startX: number
  startY: number
  at: Point
  moved: boolean
}

const drag = ref<Drag | null>(null)

/** How far (px) a press must travel before it is a drag rather than a tap. */
const DRAG_THRESHOLD_PX = 4

function clamp(point: Point): Point {
  const width = props.widthCm ?? Infinity
  const height = props.heightCm ?? Infinity
  return { x: Math.min(width, Math.max(0, point.x)), y: Math.min(height, Math.max(0, point.y)) }
}

function onPointerDown(marker: PlanMarker, event: PointerEvent): void {
  if (props.mode !== 'edit' || props.muted || event.button !== 0 || !props.toCm) return
  // The marker takes the gesture: the plan neither pans nor hands it to a tool.
  event.stopPropagation()
  event.preventDefault()
  ;(event.currentTarget as Element).setPointerCapture?.(event.pointerId)
  drag.value = {
    id: marker.placement.id,
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    at: { x: marker.placement.xCm, y: marker.placement.yCm },
    moved: false,
  }
}

function onPointerMove(event: PointerEvent): void {
  const current = drag.value
  if (!current || current.pointerId !== event.pointerId || !props.toCm) return
  event.stopPropagation()
  const moved =
    current.moved ||
    Math.hypot(event.clientX - current.startX, event.clientY - current.startY) > DRAG_THRESHOLD_PX
  drag.value = { ...current, moved, at: moved ? clamp(props.toCm(event.clientX, event.clientY)) : current.at }
}

function onPointerUp(marker: PlanMarker, event: PointerEvent): void {
  const current = drag.value
  if (!current || current.pointerId !== event.pointerId) return
  event.stopPropagation()
  drag.value = null
  if (current.moved) emit('move', marker, current.at)
  else emit('select', marker)
}

function onPointerCancel(): void {
  drag.value = null
}

function onActivate(marker: PlanMarker): void {
  if (props.mode === 'edit') emit('select', marker)
  else emit('activate', marker)
}
</script>

<template>
  <g data-slot="placement-layer" :class="muted || mode === 'preview' ? 'pointer-events-none' : undefined">
    <PlacedDevice
      v-for="marker in markers"
      :key="marker.placement.id"
      :marker="marker"
      :scale="scale"
      :compact="mode === 'preview'"
      :coarse="coarse"
      :muted="muted"
      :selected="selectedId === marker.placement.id"
      :name-on-select="mode === 'edit'"
      :dragging="drag?.id === marker.placement.id && drag.moved"
      :at="drag?.id === marker.placement.id ? drag.at : null"
      :focusable="mode !== 'preview' && !muted"
      :class="mode === 'edit' && !muted ? 'cursor-grab' : undefined"
      @activate="onActivate(marker)"
      @pointerdown="onPointerDown(marker, $event)"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp(marker, $event)"
      @pointercancel="onPointerCancel"
    />
  </g>
</template>
