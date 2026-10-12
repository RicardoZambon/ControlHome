<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'

/** The heading of a shell page: optional eyebrow, the page's only <h1>, a description and actions. */
const props = defineProps<{
  title: string
  description?: string
  eyebrow?: string
  class?: HTMLAttributes['class']
}>()
</script>

<template>
  <header
    :class="
      cn(
        'flex flex-col gap-4 pb-8 sm:flex-row sm:items-end sm:justify-between sm:gap-6 md:pb-10',
        props.class,
      )
    "
  >
    <div class="min-w-0 space-y-2">
      <p v-if="eyebrow" class="eyebrow">
        {{ eyebrow }}
      </p>
      <h1 class="font-serif text-3xl font-medium tracking-tight md:text-4xl">{{ title }}</h1>
      <p v-if="description" class="max-w-2xl text-muted-foreground">{{ description }}</p>
      <slot name="meta" />
    </div>
    <div v-if="$slots.actions" class="flex shrink-0 flex-wrap items-center gap-2">
      <slot name="actions" />
    </div>
  </header>
</template>
