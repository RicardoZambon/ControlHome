<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'

/** One labelled specimen (a primitive in one state) on the UI kit page. */
const props = defineProps<{ label: string; class?: HTMLAttributes['class'] }>()
</script>

<template>
  <div :class="cn('grid content-start gap-3 rounded-xl border bg-card p-4', props.class)">
    <p class="text-xs font-medium tracking-wide text-muted-foreground uppercase">{{ label }}</p>
    <slot />
  </div>
</template>
