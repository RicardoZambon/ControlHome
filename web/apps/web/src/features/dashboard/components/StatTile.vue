<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { Component } from 'vue'
import type { RouteLocationRaw } from 'vue-router'

/**
 * One figure of the dashboard's "at a glance" row: an icon in its tone, the figure (large, with its unit) and a line of
 * context. A link when `to` is given.
 */
defineProps<{
  icon: Component
  label: string
  value: string
  unit?: string
  hint?: string
  /** Classes of the icon bubble (`bg-state-energy/15 text-state-energy`). */
  tone: string
  to?: RouteLocationRaw
  /** Something needs attention: the tile is outlined in the warning color. */
  attention?: boolean
}>()
</script>

<template>
  <component
    :is="to ? 'RouterLink' : 'div'"
    :to="to"
    :class="
      cn(
        'group relative flex min-h-32 flex-col justify-between gap-4 overflow-hidden rounded-2xl border bg-card p-4 shadow-xs sm:p-5',
        to &&
          'transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
        attention && 'border-state-warn/60',
      )
    "
    data-slot="stat-tile"
  >
    <div class="flex items-center justify-between gap-3">
      <span class="text-sm text-muted-foreground">{{ label }}</span>
      <span
        :class="cn('flex size-8 shrink-0 items-center justify-center rounded-lg', tone)"
        aria-hidden="true"
      >
        <component :is="icon" class="size-4" />
      </span>
    </div>
    <div class="grid gap-1">
      <p class="flex items-baseline gap-1.5 leading-none">
        <span
          class="font-serif text-3xl font-medium tracking-tight tabular-nums sm:text-4xl"
          data-slot="stat-value"
        >
          {{ value }}
        </span>
        <span v-if="unit" class="text-sm font-medium text-muted-foreground">{{ unit }}</span>
      </p>
      <p v-if="hint" class="truncate text-xs text-muted-foreground">{{ hint }}</p>
    </div>
  </component>
</template>
