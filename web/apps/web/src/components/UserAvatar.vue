<script setup lang="ts">
import { Avatar, AvatarFallback, cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import { hashOf, initialsOf } from '../lib/format'

/**
 * Initials on a room color picked from the user name, so each person keeps the same color everywhere. `inactive`
 * (a deactivated account): neutral muted colors instead, still readable (never faded with opacity).
 */
const props = defineProps<{
  name: string
  seed?: string
  large?: boolean
  inactive?: boolean
  class?: HTMLAttributes['class']
}>()

// Full class names, so Tailwind generates them.
const tones = [
  'bg-room-1 text-room-1-foreground',
  'bg-room-2 text-room-2-foreground',
  'bg-room-3 text-room-3-foreground',
  'bg-room-4 text-room-4-foreground',
  'bg-room-5 text-room-5-foreground',
  'bg-room-6 text-room-6-foreground',
  'bg-room-7 text-room-7-foreground',
  'bg-room-8 text-room-8-foreground',
]

const tone = computed(() =>
  props.inactive ? 'bg-muted text-muted-foreground' : tones[hashOf(props.seed ?? props.name) % tones.length],
)
const initials = computed(() => initialsOf(props.name))
</script>

<template>
  <Avatar :class="cn('size-9', props.class)">
    <AvatarFallback
      :class="cn(large ? 'text-lg' : 'text-xs', 'font-semibold tracking-wide', tone)"
      aria-hidden="true"
    >
      {{ initials }}
    </AvatarFallback>
  </Avatar>
</template>
