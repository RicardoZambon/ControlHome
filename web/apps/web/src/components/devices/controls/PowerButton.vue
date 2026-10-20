<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { Power } from '@lucide/vue'
import type { Component, HTMLAttributes } from 'vue'

/**
 * The round power button of a device (a switch: `role="switch"`, `aria-checked`). On, it is a solid state-on disc
 * with a soft halo; off, a quiet card disc; unknown, a dashed outline. While a command is pending a ring spins around
 * it. `sm` is the tiles' quick action (44 px), `lg` the device page's main control.
 */
const props = withDefaults(
  defineProps<{
    on?: boolean
    label: string
    pending?: boolean
    disabled?: boolean
    size?: 'sm' | 'lg'
    icon?: Component
    class?: HTMLAttributes['class']
  }>(),
  { size: 'sm' },
)
const emit = defineEmits<{ toggle: [] }>()
</script>

<template>
  <button
    type="button"
    role="switch"
    :aria-checked="on === true"
    :aria-label="label"
    :aria-busy="pending || undefined"
    :disabled="disabled"
    data-slot="power-button"
    :data-state="on === undefined ? 'unknown' : on ? 'on' : 'off'"
    :class="
      cn(
        'relative inline-flex shrink-0 touch-manipulation items-center justify-center rounded-full transition-[background-color,color,box-shadow,transform] duration-300 outline-none select-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-50 motion-safe:active:scale-95',
        size === 'sm' ? 'size-11 [&_svg]:size-5' : 'size-24 [&_svg]:size-9',
        on === true &&
          'bg-state-on text-state-on-foreground shadow-lg ring-6 shadow-state-on/30 ring-state-on/15 hover:bg-state-on/95',
        on === false &&
          'border bg-card text-muted-foreground shadow-xs hover:bg-accent hover:text-accent-foreground',
        on === undefined && 'border border-dashed bg-card text-muted-foreground',
        props.class,
      )
    "
    @click="emit('toggle')"
  >
    <component :is="icon ?? Power" aria-hidden="true" :stroke-width="2.25" />
    <span
      v-if="pending"
      aria-hidden="true"
      :class="
        cn(
          'pointer-events-none absolute rounded-full border-2 border-transparent motion-safe:animate-spin',
          size === 'sm' ? '-inset-1' : '-inset-2',
          on ? 'border-t-state-on' : 'border-t-primary',
        )
      "
    />
  </button>
</template>
