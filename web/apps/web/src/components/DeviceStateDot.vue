<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'

/**
 * A device's state as a small dot, always next to its word: filled for on and off, a hollow ring for offline, so off
 * and unreachable never differ by color alone.
 */
const props = defineProps<{ state: 'on' | 'off' | 'offline'; class?: HTMLAttributes['class'] }>()
</script>

<template>
  <span
    aria-hidden="true"
    :class="
      cn(
        'inline-block size-2.5 shrink-0 rounded-full',
        props.state === 'on' && 'bg-state-on',
        props.state === 'off' && 'bg-state-off',
        props.state === 'offline' && 'border-2 border-state-offline bg-transparent',
        props.class,
      )
    "
  />
</template>
