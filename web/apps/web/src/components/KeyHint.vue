<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'

/**
 * A keyboard shortcut in a tooltip or a hint: each key in its own `<kbd>` (`keys="Ctrl+Z"` → Ctrl Z; pass an array for
 * keys that contain "+"). Inherits the text color, so it reads on tooltips (inverted) and on surfaces alike.
 */
const props = defineProps<{ keys: string | readonly string[]; class?: HTMLAttributes['class'] }>()

const list = computed(() => (typeof props.keys === 'string' ? props.keys.split('+') : props.keys))
</script>

<template>
  <span :class="cn('inline-flex items-center gap-0.5 align-middle', props.class)">
    <kbd
      v-for="key in list"
      :key="key"
      class="inline-flex h-4.5 min-w-4.5 items-center justify-center rounded-sm border border-current/40 px-1 font-sans text-[0.625rem] leading-none font-medium"
    >
      {{ key }}
    </kbd>
  </span>
</template>
