<script setup lang="ts">
import { Badge, cn } from '@controlhome/ui'
import { CircleHelp, Unlink, Wifi, WifiOff } from '@lucide/vue'
import { computed, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * A device's availability as a badge: Online (green dot), Offline (WifiOff, slate violet), Unknown (a question mark),
 * or "No longer reported" for an orphaned device. Shape and word always come with the color.
 */
const props = defineProps<{
  availability: string
  orphaned?: boolean
  class?: HTMLAttributes['class']
}>()
const { t, te } = useI18n()

const kind = computed(() =>
  props.orphaned
    ? 'orphaned'
    : props.availability === 'Online'
      ? 'online'
      : props.availability === 'Offline'
        ? 'offline'
        : 'unknown',
)
const label = computed(() => {
  if (props.orphaned) return t('device.orphaned')
  const key = `device.availability.${props.availability}`
  return te(key) ? t(key) : t('device.availability.Unknown')
})
</script>

<template>
  <Badge
    variant="outline"
    :data-availability="kind"
    :class="
      cn(
        'gap-1.5 bg-card py-1 pr-2.5 pl-2 font-medium',
        kind === 'online' && 'border-state-on/50',
        kind === 'offline' && 'border-dashed border-state-offline/70',
        kind === 'orphaned' && 'border-state-warn/60',
        props.class,
      )
    "
  >
    <Wifi v-if="kind === 'online'" class="text-state-on" aria-hidden="true" />
    <WifiOff v-else-if="kind === 'offline'" class="text-state-offline" aria-hidden="true" />
    <Unlink v-else-if="kind === 'orphaned'" class="text-state-warn" aria-hidden="true" />
    <CircleHelp v-else class="text-muted-foreground" aria-hidden="true" />
    {{ label }}
  </Badge>
</template>
