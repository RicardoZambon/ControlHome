<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'

/** The ControlHome mark: a house outline with a control point. Decorative; pair it with the product name. */
const props = defineProps<{ class?: HTMLAttributes['class'] }>()
</script>

<template>
  <span
    aria-hidden="true"
    :class="
      cn(
        'inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-sm',
        props.class,
      )
    "
  >
    <svg viewBox="0 0 32 32" class="size-[72%]" fill="none">
      <path
        d="M7 15.2 16 8l9 7.2V24a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 7 24z"
        stroke="currentColor"
        stroke-width="2.2"
        stroke-linejoin="round"
      />
      <path
        d="M12.6 17.4a4.8 4.8 0 0 1 6.8 0"
        stroke="currentColor"
        stroke-width="2.2"
        stroke-linecap="round"
      />
      <circle cx="16" cy="20.4" r="1.7" fill="currentColor" />
    </svg>
  </span>
</template>
