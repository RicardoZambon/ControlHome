<script setup lang="ts">
import type { HTMLAttributes } from 'vue'
import { PanelLeft } from '@lucide/vue'
import { cn } from '@controlhome/ui/lib/utils'
import { Button } from '@controlhome/ui/components/ui/button'
import { useSidebar } from './utils'

const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes['class']
    /** Screen-reader label; pass a translated string. */
    label?: string
  }>(),
  {
    label: 'Toggle Sidebar',
  },
)

const { toggleSidebar } = useSidebar()
</script>

<template>
  <Button
    data-sidebar="trigger"
    data-slot="sidebar-trigger"
    variant="ghost"
    size="icon"
    :class="cn('h-7 w-7', props.class)"
    @click="toggleSidebar"
  >
    <PanelLeft />
    <span class="sr-only">{{ label }}</span>
  </Button>
</template>
