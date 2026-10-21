<script setup lang="ts">
import type { EnergyHourlyDto } from '@controlhome/api-client'
import { cn, useChartTheme } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { bucketEnergy, energyOption, energyUnit } from './chartOptions'
import './echarts'
import { useChartPalette } from './useChartPalette'

/**
 * Energy used as bars, per hour over a day or per day over longer ranges (from `EnergyHourly`, the one source of every
 * energy view). Hours estimated from power readings are drawn lighter and say so in the tooltip.
 */
const props = defineProps<{
  rows: readonly EnergyHourlyDto[]
  bucket: 'hour' | 'day'
  window: { min: number; max: number }
  label: string
  class?: HTMLAttributes['class']
}>()

const { t, locale } = useI18n()
const theme = useChartTheme()
const palette = useChartPalette('state-energy')

const buckets = computed(() => bucketEnergy(props.rows, props.bucket))
const unit = computed(() => energyUnit(buckets.value))
const number = computed(
  () => new Intl.NumberFormat(locale.value, { maximumFractionDigits: unit.value === 'kWh' ? 2 : 0 }),
)
const formats = computed(() => ({
  hour: new Intl.DateTimeFormat(locale.value, { hour: '2-digit', minute: '2-digit' }),
  day: new Intl.DateTimeFormat(locale.value, { day: 'numeric', month: 'short' }),
}))

const option = computed(() =>
  energyOption({
    buckets: buckets.value,
    bucket: props.bucket,
    window: props.window,
    palette: palette.value,
    unit: unit.value,
    formatNumber: (value) => number.value.format(value),
    formatTime: (time, bucket) => formats.value[bucket].format(time),
    estimatedLabel: t('history.estimated'),
    name: props.label,
  }),
)
</script>

<template>
  <div
    :class="cn('relative h-52 w-full', props.class)"
    data-slot="energy-bars"
    :data-buckets="buckets.length"
  >
    <VChart
      v-if="buckets.length > 0"
      :option="option"
      :theme="theme"
      autoresize
      role="img"
      :aria-label="label"
      class="size-full"
    />
    <div
      v-else
      class="grid size-full place-items-center rounded-xl border border-dashed text-sm text-muted-foreground"
      data-slot="history-empty"
    >
      {{ t('history.noEnergy') }}
    </div>
  </div>
</template>
