<script lang="ts" setup>
import type { HTMLAttributes } from 'vue'
import { MoreHorizontal } from '@lucide/vue'
import { cn } from '@controlhome/ui/lib/utils'

const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes['class']
    /** Screen-reader text for the collapsed items; pass a translated string. */
    label?: string
  }>(),
  {
    label: 'More',
  },
)
</script>

<template>
  <span
    data-slot="breadcrumb-ellipsis"
    role="presentation"
    aria-hidden="true"
    :class="cn('flex size-9 items-center justify-center', props.class)"
  >
    <slot>
      <MoreHorizontal class="size-4" />
    </slot>
    <span class="sr-only">{{ label }}</span>
  </span>
</template>
