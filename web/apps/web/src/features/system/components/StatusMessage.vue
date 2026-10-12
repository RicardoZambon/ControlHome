<script setup lang="ts">
import { Button } from '@controlhome/ui'
import { ArrowLeft } from '@lucide/vue'
import type { Component } from 'vue'

/** A centered status page (not found, forbidden) with a way back home. */
defineProps<{ icon: Component; code: string; heading: string; description: string; back: string }>()
</script>

<template>
  <section class="mx-auto flex max-w-xl flex-col items-center py-12 text-center sm:py-20">
    <div class="relative mb-8">
      <div class="absolute inset-0 scale-150 rounded-full bg-accent blur-2xl" aria-hidden="true" />
      <span
        class="relative flex size-20 items-center justify-center rounded-3xl border bg-card text-primary shadow-md"
        aria-hidden="true"
      >
        <component :is="icon" class="size-9" />
      </span>
    </div>
    <p class="mb-2 font-mono text-sm tracking-[0.3em] text-muted-foreground">{{ code }}</p>
    <h1 class="font-serif text-4xl font-medium tracking-tight text-balance sm:text-5xl">{{ heading }}</h1>
    <p class="mt-4 max-w-md text-pretty text-muted-foreground">{{ description }}</p>
    <Button as-child size="lg" class="mt-8">
      <RouterLink :to="{ name: 'dashboard' }">
        <ArrowLeft />
        {{ back }}
      </RouterLink>
    </Button>
  </section>
</template>
