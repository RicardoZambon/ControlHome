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
    :class="cn(props.class)"
    @click="toggleSidebar"
  >
    <!-- The icon: PanelLeft by default (collapse on desktop); pass a Menu icon where it opens the mobile sheet. -->
    <slot><PanelLeft /></slot>
    <span class="sr-only">{{ label }}</span>
  </Button>
</template>
