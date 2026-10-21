<script setup lang="ts">
import { computed } from 'vue'
import PowerButton from '../controls/PowerButton.vue'
import { pendingLabel } from '../pendingLabel'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `switch`: the power button — the tile quick action, and on the device page of a plain switch (a plug) the big button
 * beside its state. Devices with an adjustment (a light's brightness, an AC's climate) show power as a toggle in that
 * card's header instead (`PowerToggle`).
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te, locale } = control

const on = computed(() => control.state.bool('on'))
const pendingCommand = computed(() => control.pendingCommand())
const pending = computed(() => pendingCommand.value !== undefined)
const stateText = computed(() =>
  pendingCommand.value
    ? pendingLabel(pendingCommand.value, t, te, locale.value)
    : on.value === undefined
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
  <div v-else class="flex items-center gap-6" data-slot="switch-control">
    <PowerButton
      size="lg"
      :on="on"
      :label="t('device.switch.label', { name: control.name.value })"
      :pending="pending"
      :disabled="!control.enabled.value"
      @toggle="toggle"
    />
    <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite" data-slot="switch-state">
      {{ stateText }}
    </p>
  </div>
</template>
