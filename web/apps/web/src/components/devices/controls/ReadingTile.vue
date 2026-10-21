<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { Component, HTMLAttributes } from 'vue'
import type { Tone } from '../deviceState'
import { TONES } from '../tone'

/**
 * One sensor reading on the device page: an icon tinted by the state, the reading's name, its value (large, with the
 * unit set smaller) and an optional caption. `role="group"` with the name, so screen readers hear "Temperature, 22,5 °C".
 */
const props = withDefaults(
  defineProps<{
    icon: Component
    label: string
    value: string
    unit?: string
    tone?: Tone
    caption?: string
    /** The value is a word ("Closed"), not a number: a smaller size. */
    word?: boolean
    class?: HTMLAttributes['class']
  }>(),
  { tone: 'neutral' },
)
</script>

<template>
  <div
    role="group"
    :aria-label="label"
    data-slot="reading"
    :data-tone="tone"
    :class="
      cn(
        'relative flex flex-col justify-between gap-4 overflow-hidden rounded-2xl border bg-card p-4 shadow-xs',
        tone !== 'neutral' && tone !== 'off' && TONES[tone].border,
        props.class,
      )
    "
  >
    <div class="flex items-center gap-2.5">
      <span
        :class="
          cn(
            'inline-flex size-8 shrink-0 items-center justify-center rounded-lg [&_svg]:size-4',
            TONES[tone].bubble,
            TONES[tone].icon,
          )
        "
        aria-hidden="true"
      >
        <component :is="icon" />
      </span>
      <span class="line-clamp-2 text-sm leading-snug text-muted-foreground">{{ label }}</span>
    </div>
    <div class="grid gap-1">
      <p :class="cn('flex items-baseline leading-none', unit === '%' ? 'gap-0.5' : 'gap-1.5')">
        <span
          :class="cn('font-serif font-medium tracking-tight tabular-nums', word ? 'text-2xl' : 'text-3xl')"
          data-slot="reading-value"
          >{{ value }}</span
        >
        <span v-if="unit" class="text-sm font-medium text-muted-foreground">{{ unit }}</span>
      </p>
      <p v-if="caption" class="truncate text-xs text-muted-foreground">{{ caption }}</p>
    </div>
  </div>
</template>
