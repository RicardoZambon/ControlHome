<script setup lang="ts">
import { computed } from 'vue'
import { gridSteps } from './usePanZoom'

/**
 * The drafting grid over the floor: hairlines every grid step (thinned out when zoomed out) and stronger lines every
 * metre. Lines keep a 1 px width at any zoom (`vector-effect: non-scaling-stroke`).
 */
const props = defineProps<{
  widthCm: number
  heightCm: number
  gridCm: number
  /** Screen px per cm. */
  scale: number
  /** Only faint metre lines (dashboard-size previews, where the editor grid would be noise). */
  minimal?: boolean
}>()

/** Above this many lines per direction the grid is too dense to be useful (and to draw). */
const MAX_LINES = 400

const steps = computed(() => gridSteps(props.gridCm, props.scale))

function lines(step: number, skip: number | null): string {
  const parts: string[] = []
  const { widthCm: width, heightCm: height } = props
  if (width / step > MAX_LINES || height / step > MAX_LINES) return ''
  for (let x = step; x < width; x += step) {
    if (skip !== null && Math.abs(x / skip - Math.round(x / skip)) < 1e-9) continue
    parts.push(`M${x} 0V${height}`)
  }
  for (let y = step; y < height; y += step) {
    if (skip !== null && Math.abs(y / skip - Math.round(y / skip)) < 1e-9) continue
    parts.push(`M0 ${y}H${width}`)
  }
  return parts.join('')
}

const minorPath = computed(() => (props.minimal ? '' : lines(steps.value.minor, steps.value.major)))
const majorPath = computed(() => lines(steps.value.major, null))
</script>

<template>
  <g class="pointer-events-none" aria-hidden="true" fill="none">
    <path
      v-if="minorPath"
      :d="minorPath"
      class="stroke-border"
      stroke-width="1"
      vector-effect="non-scaling-stroke"
      shape-rendering="crispEdges"
    />
    <path
      v-if="majorPath"
      :d="majorPath"
      :class="minimal ? 'stroke-border/60' : 'stroke-muted-foreground/25'"
      stroke-width="1"
      vector-effect="non-scaling-stroke"
      shape-rendering="crispEdges"
    />
  </g>
</template>
