<script setup lang="ts">
import { Badge, cn } from '@controlhome/ui'
import { Activity } from '@lucide/vue'
import type { HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'

/** "Motion" on a camera picture while the device reports motion. */
const props = defineProps<{ class?: HTMLAttributes['class'] }>()
const { t } = useI18n()
</script>

<template>
  <Badge
    data-slot="motion-badge"
    :class="cn('border-transparent bg-state-warn text-state-warn-foreground shadow-sm', props.class)"
  >
    <Activity aria-hidden="true" />
    {{ t('device.camera.motion') }}
  </Badge>
</template>
