<script setup lang="ts">
import {
  BatteryFull,
  BatteryLow,
  BatteryMedium,
  BatteryWarning,
  Droplets,
  PlugZap,
  Thermometer,
  Zap,
} from '@lucide/vue'
import { computed, type Component } from 'vue'
import ReadingTile from '../controls/ReadingTile.vue'
import { LOW_BATTERY_PERCENT, type Tone } from '../deviceState'
import { capabilityLabel, measurement } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'
import { useUpdated } from './useUpdated'

/** One-number sensors: `power` (W), `energy` (Wh, shown as kWh when large), `temperature`, `humidity`, `battery`. */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te, locale } = control
const updated = useUpdated()

interface Config {
  key: string
  unit: string
  icon: (value: number | undefined) => Component
  tone: (value: number | undefined) => Tone
}

const CONFIGS: Record<string, Config> = {
  power: {
    key: 'power',
    unit: 'W',
    icon: () => Zap,
    tone: (value) => ((value ?? 0) > 0 ? 'energy' : 'neutral'),
  },
  energy: { key: 'energy', unit: 'Wh', icon: () => PlugZap, tone: () => 'neutral' },
  temperature: { key: 'temperature', unit: '°C', icon: () => Thermometer, tone: () => 'neutral' },
  humidity: { key: 'humidity', unit: '%', icon: () => Droplets, tone: () => 'neutral' },
  battery: {
    key: 'battery',
    unit: '%',
    icon: (value) =>
      value === undefined || value >= 60
        ? BatteryFull
        : value >= 30
          ? BatteryMedium
          : value >= LOW_BATTERY_PERCENT
            ? BatteryLow
            : BatteryWarning,
    tone: (value) => (value !== undefined && value < LOW_BATTERY_PERCENT ? 'warn' : 'neutral'),
  },
}

const config = computed(() => CONFIGS[props.capability.capabilityId] ?? CONFIGS.temperature!)
const value = computed(() => control.state.number(config.value.key))
const parts = computed(() =>
  value.value === undefined ? undefined : measurement(value.value, config.value.unit, locale.value),
)
const caption = computed(() => {
  if (
    props.capability.capabilityId === 'battery' &&
    value.value !== undefined &&
    value.value < LOW_BATTERY_PERCENT
  )
    return t('device.reading.replaceBattery')
  return updated(control.state.live(config.value.key))
})
</script>

<template>
  <ReadingTile
    :icon="config.icon(value)"
    :label="capabilityLabel(capability.capabilityId, t, te)"
    :value="parts?.value ?? t('device.noValue')"
    :unit="parts?.unit"
    :tone="config.tone(value)"
    :caption="caption"
  />
</template>
