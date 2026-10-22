<script setup lang="ts">
import { Switch } from '@controlhome/ui'
import { computed } from 'vue'
import { pendingLabel } from '../pendingLabel'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `switch` folded into the header of the device's main control card (the light's brightness, the AC's climate): the
 * state in a word and a switch, so power and the adjustment sit together instead of a card around one button.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te, locale } = control

const on = computed(() => control.state.bool('on'))
const pending = computed(() => control.pendingCommand())
const stateText = computed(() =>
  pending.value
    ? pendingLabel(pending.value, t, te, locale.value)
    : on.value === undefined
      ? t('device.state.unknown')
      : on.value
        ? t('device.state.on')
        : t('device.state.off'),
)

function set(next: boolean): void {
  const command = next ? 'turnOn' : 'turnOff'
  void control.send(control.hasCommand(command) ? command : 'toggle')
}
</script>

<template>
  <div class="flex items-center gap-3" data-slot="power-toggle">
    <span class="text-sm font-medium text-muted-foreground" aria-live="polite">{{ stateText }}</span>
    <Switch
      :model-value="on === true"
      :disabled="!control.enabled.value"
      :aria-label="t('device.switch.label', { name: control.name.value })"
      :aria-busy="pending ? true : undefined"
      class="data-[state=checked]:bg-state-on"
      @update:model-value="(value) => set(value === true)"
    />
  </div>
</template>
