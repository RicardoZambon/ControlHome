<script lang="ts" setup>
import type { HTMLAttributes } from 'vue'

const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes['class']
    /** Accessible name of the navigation landmark; pass a translated string. */
    label?: string
  }>(),
  {
    label: 'Breadcrumb',
  },
)
</script>

<template>
  <nav :aria-label="label" data-slot="breadcrumb" :class="props.class">
    <slot />
  </nav>
</template>
