<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import FloorPlanCanvas, { type FloorPlan } from './FloorPlanCanvas.vue'

/**
 * A floor plan as a picture: the whole floor fitted into the box, no pan or zoom, small labels. For cards (floor list,
 * dashboard), which link to the full plan themselves. The box keeps the floor's proportions (between 4:3 and 16:9).
 */
const props = defineProps<{ floor: FloorPlan; class?: HTMLAttributes['class'] }>()

const aspect = computed(() => {
  const ratio = props.floor.widthCm / Math.max(props.floor.heightCm, 1)
  return Math.min(16 / 9, Math.max(4 / 3, ratio))
})
</script>

<template>
  <div :class="cn('relative w-full', props.class)" :style="{ aspectRatio: aspect }">
    <FloorPlanCanvas
      :floor="floor"
      :interactive="false"
      :focusable-rooms="false"
      compact
      class="absolute inset-0"
    />
  </div>
</template>
