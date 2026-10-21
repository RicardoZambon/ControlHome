<script setup lang="ts">
import { computed } from 'vue'
import { resolveControl } from './registry'
import type { CapabilityControlProps } from './types'

/** Renders the registered control of a capability (or the generic one). Import it from `./index` (built-ins registered). */
const props = withDefaults(defineProps<CapabilityControlProps>(), { variant: 'detail' })
const resolved = computed(() => resolveControl(props.capability.capabilityId))
</script>

<template>
  <component
    :is="resolved.component"
    :device="device"
    :endpoint="endpoint"
    :capability="capability"
    :variant="variant"
  />
</template>
