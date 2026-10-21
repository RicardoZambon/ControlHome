<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import type { Tone } from './deviceState'
import { deviceIcon } from './deviceIcons'
import { TONES } from './tone'

/** A device's icon in a soft bubble tinted by its state (decorative: the state is always in words nearby). */
const props = withDefaults(
  defineProps<{
    icon?: string | null
    deviceClass?: string | null
    capabilities?: readonly string[]
    /** Stands open (an unlocked lock, an open door): the icon shows it open. */
    open?: boolean
    tone?: Tone
    size?: 'sm' | 'md' | 'lg'
    class?: HTMLAttributes['class']
  }>(),
  { tone: 'neutral', size: 'md' },
)

const component = computed(() =>
  deviceIcon({
    icon: props.icon,
    deviceClass: props.deviceClass,
    capabilities: props.capabilities,
    open: props.open,
  }),
)
</script>

<template>
  <span
    aria-hidden="true"
    data-slot="device-icon"
    :class="
      cn(
        'inline-flex shrink-0 items-center justify-center transition-colors duration-300',
        size === 'sm' && 'size-8 rounded-lg [&_svg]:size-4',
        size === 'md' && 'size-11 rounded-xl [&_svg]:size-5',
        size === 'lg' && 'size-16 rounded-2xl [&_svg]:size-8',
        TONES[tone].bubble,
        TONES[tone].icon,
        props.class,
      )
    "
  >
    <component :is="component" />
  </span>
</template>
