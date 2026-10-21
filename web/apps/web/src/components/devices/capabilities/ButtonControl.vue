<script setup lang="ts">
import { CircleDot } from '@lucide/vue'
import { computed } from 'vue'
import ReadingTile from '../controls/ReadingTile.vue'
import { capabilityLabel } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'
import { useUpdated } from './useUpdated'

/** `button`: the last press (single, double, long) and when it happened. Buttons only send events. */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te } = control
const updated = useUpdated()

const press = computed(() => control.state.string('press'))
</script>

<template>
  <ReadingTile
    :icon="CircleDot"
    :label="capabilityLabel(capability.capabilityId, t, te)"
    :value="press ? control.valueLabel('press', press) : t('device.button.never')"
    :tone="press ? 'info' : 'neutral'"
    :caption="press ? updated(control.state.live('press')) : undefined"
    word
  />
</template>
