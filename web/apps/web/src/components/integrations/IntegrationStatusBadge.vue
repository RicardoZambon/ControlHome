<script setup lang="ts">
import { Badge, cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { statusStyle } from './integrationStatus'

/** An integration's status as a badge: a colored dot (pulsing while it connects) and the status in words. */
const props = defineProps<{ status: string; class?: HTMLAttributes['class'] }>()
const { t, te } = useI18n()

const style = computed(() => statusStyle(props.status))
const label = computed(() =>
  te(`integrationStatus.${props.status}`) ? t(`integrationStatus.${props.status}`) : props.status,
)
</script>

<template>
  <Badge
    variant="outline"
    :class="cn('gap-1.5 bg-card py-1', style.attention && 'border-state-warn/50', props.class)"
    data-slot="integration-status"
    :data-status="status"
  >
    <span class="relative flex size-2" aria-hidden="true">
      <span
        v-if="style.pulse"
        :class="cn('absolute inset-0 rounded-full opacity-60 motion-safe:animate-ping', style.tone)"
      />
      <span :class="cn('relative size-2 rounded-full', style.tone)" />
    </span>
    {{ label }}
  </Badge>
</template>
