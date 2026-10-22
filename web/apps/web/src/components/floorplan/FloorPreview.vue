<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import FloorPlanCanvas, { type FloorPlan } from './FloorPlanCanvas.vue'
import PlacementLayer from './PlacementLayer.vue'
import type { PlanMarker } from './placements'

/**
 * A floor plan as a picture: the floor (or `fitTo`, what is drawn on it) fitted into the box, no pan or zoom, small
 * labels, and (when given) its devices as small live dots. For cards (floor list, dashboard), which link to the full plan
 * themselves. The box keeps the picture's proportions (between 4:3 and 16:9).
 */
const props = defineProps<{
  floor: FloorPlan
  markers?: readonly PlanMarker[]
  /** The part of the floor to show (cm), e.g. `planBounds(floor)`; the whole floor by default. */
  fitTo?: { x: number; y: number; width: number; height: number } | null
  class?: HTMLAttributes['class']
}>()

const aspect = computed(() => {
  const width = props.fitTo?.width ?? props.floor.widthCm
  const height = props.fitTo?.height ?? props.floor.heightCm
  return Math.min(16 / 9, Math.max(4 / 3, width / Math.max(height, 1)))
})
</script>

<template>
  <div :class="cn('relative w-full', props.class)" :style="{ aspectRatio: aspect }">
    <FloorPlanCanvas
      :floor="floor"
      :fit-to="fitTo"
      :interactive="false"
      :focusable-rooms="false"
      compact
      class="absolute inset-0"
    >
      <template v-if="markers && markers.length > 0" #overlay="{ scale }">
        <PlacementLayer :markers="markers" :scale="scale" mode="preview" />
      </template>
    </FloorPlanCanvas>
  </div>
</template>
