<script setup lang="ts">
import { Cloud, Leaf, Wind } from '@lucide/vue'
import { computed, type Component } from 'vue'
import ReadingTile from '../controls/ReadingTile.vue'
import { airQualityLevel, type AirQualityLevel, type Tone } from '../deviceState'
import { measurement } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/** `airQuality`: PM2.5, CO₂ and VOC, each colored by its band (good, moderate, poor). */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, locale } = control

const TONES: Record<AirQualityLevel, Tone> = { good: 'on', moderate: 'warn', poor: 'danger' }

interface Reading {
  key: 'pm25' | 'co2' | 'voc'
  unit: string
  icon: Component
}

const READINGS: Reading[] = [
  { key: 'pm25', unit: 'µg/m³', icon: Cloud },
  { key: 'co2', unit: 'ppm', icon: Wind },
  { key: 'voc', unit: 'ppb', icon: Leaf },
]

const tiles = computed(() =>
  READINGS.filter((reading) => control.state.has(reading.key)).map((reading) => {
    const value = control.state.number(reading.key)
    const level = value === undefined ? undefined : airQualityLevel({ [reading.key]: value })
    const parts = value === undefined ? undefined : measurement(value, reading.unit, locale.value)
    return {
      ...reading,
      label: control.attributeLabel(reading.key),
      value: parts?.value ?? t('device.noValue'),
      unit: parts?.unit,
      tone: level ? TONES[level] : ('neutral' as Tone),
      caption: level ? t(`device.airQuality.${level}`) : undefined,
    }
  }),
)
</script>

<template>
  <div class="contents" data-slot="air-quality">
    <ReadingTile
      v-for="tile in tiles"
      :key="tile.key"
      :icon="tile.icon"
      :label="tile.label"
      :value="tile.value"
      :unit="tile.unit"
      :tone="tile.tone"
      :caption="tile.caption"
    />
  </div>
</template>
