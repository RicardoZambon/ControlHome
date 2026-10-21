<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import PlacedDevice from './PlacedDevice.vue'
import type { Point } from './geometry'
import { markerRadiusPx } from './markerTone'
import type { MarkerCluster, PlanMarker } from './placements'

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
    /**
     * View mode: the markers grouped as drawn at this zoom (`clusterMarkers`); a group of several is one bubble with
     * their count (`expand` zooms in on it).
     */
    clusters?: readonly MarkerCluster<PlanMarker>[]
  }>(),
  { mode: 'view', selectedId: null, coarse: false, muted: false, clusters: undefined },
)

const emit = defineEmits<{
  activate: [marker: PlanMarker]
  select: [marker: PlanMarker]
  move: [marker: PlanMarker, point: Point]
  expand: [cluster: MarkerCluster<PlanMarker>]
}>()

const { t } = useI18n()
const clusterRadius = computed(() => markerRadiusPx(props.scale, props.coarse) + 2)
/** What is drawn one by one: every marker, or (grouped view) the groups of one. */
const singles = computed(() =>
  props.clusters
    ? props.clusters.filter((cluster) => cluster.members.length === 1).map((cluster) => cluster.members[0]!)
    : props.markers,
)
const groups = computed(() => (props.clusters ?? []).filter((cluster) => cluster.members.length > 1))
const px = (value: number) => value / props.scale

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
    <!-- Devices too close to tell apart at this zoom: one bubble with their count; it zooms in on them -->
    <g
      v-for="group in groups"
      :key="`c${group.members[0]!.placement.id}`"
      :transform="`translate(${group.center.x} ${group.center.y})`"
      role="button"
      tabindex="0"
      :aria-label="
        t('floorplan.devices.group', {
          count: group.members.length,
          names: group.members.map((member) => member.name).join(', '),
        })
      "
      class="group/cluster cursor-pointer outline-none"
      data-slot="placed-cluster"
      :data-count="group.members.length"
      @keydown.enter.prevent="emit('expand', group)"
      @keydown.space.prevent="emit('expand', group)"
    >
      <title>{{ group.members.map((member) => member.name).join(', ') }}</title>
      <circle :r="px(Math.max(clusterRadius, 22))" class="fill-transparent" />
      <circle
        :r="px(clusterRadius + 4)"
        fill="none"
        stroke-width="2"
        vector-effect="non-scaling-stroke"
        class="stroke-ring opacity-0 transition-opacity group-focus-visible/cluster:opacity-100"
      />
      <circle :r="px(clusterRadius)" class="fill-primary stroke-card drop-shadow-sm" :stroke-width="px(2)" />
      <text
        text-anchor="middle"
        dominant-baseline="central"
        :font-size="px(clusterRadius * 0.95)"
        class="pointer-events-none fill-primary-foreground font-sans font-semibold tabular-nums"
      >
        {{ group.members.length }}
      </text>
    </g>
    <PlacedDevice
      v-for="marker in singles"
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
