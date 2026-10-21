<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import { lightLayers, type LightColor } from '../lightColor'

/**
 * The color a light shows, as a disc: two stacked token fills (see lightColor.ts) and, for a desaturated color, a
 * neutral white on top. Decorative unless given a label.
 */
const props = defineProps<{ color: LightColor; label?: string; class?: HTMLAttributes['class'] }>()

const layers = computed(() => lightLayers(props.color))
const whiteness = computed(() =>
  props.color.mode === 'color' ? Math.min(1, Math.max(0, 1 - props.color.saturation / 100)) : 0,
)
</script>

<template>
  <span
    :role="label ? 'img' : undefined"
    :aria-label="label"
    :aria-hidden="label ? undefined : 'true'"
    data-slot="color-swatch"
    :class="
      cn(
        'relative inline-block size-6 shrink-0 overflow-hidden rounded-full ring-1 ring-border',
        layers.base,
        props.class,
      )
    "
  >
    <span class="absolute inset-0" :class="layers.over" :style="{ opacity: layers.mix }" />
    <span class="absolute inset-0 bg-kelvin-neutral" :style="{ opacity: whiteness }" />
  </span>
</template>
