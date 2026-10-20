<script setup lang="ts">
import { computed } from 'vue'
import PowerButton from '../controls/PowerButton.vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/** `switch`: the power button (tile quick action, and the big one on the device page). */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const on = computed(() => control.state.bool('on'))
const pending = computed(() => control.pending())
const stateText = computed(() =>
  on.value === undefined
    ? t('device.state.unknown')
    : on.value
      ? t('device.state.on')
      : t('device.state.off'),
)

function toggle(): void {
  const next = on.value !== true
  const command = next ? 'turnOn' : 'turnOff'
  void control.send(control.hasCommand(command) ? command : 'toggle')
}
</script>

<template>
  <PowerButton
    v-if="variant === 'tile'"
    :on="on"
    :label="t('device.switch.label', { name: control.name.value })"
    :pending="pending"
    :disabled="!control.enabled.value"
    @toggle="toggle"
  />
  <div v-else class="flex flex-col items-center gap-4 py-2" data-slot="switch-control">
    <PowerButton
      size="lg"
      :on="on"
      :label="t('device.switch.label', { name: control.name.value })"
      :pending="pending"
      :disabled="!control.enabled.value"
      @toggle="toggle"
    />
    <p class="font-serif text-2xl font-medium tracking-tight" aria-live="polite">
      {{ pending ? t('device.state.pending') : stateText }}
    </p>
  </div>
</template>
