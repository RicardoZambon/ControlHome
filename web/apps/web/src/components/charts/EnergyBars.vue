<script setup lang="ts">
import type { EnergyHourlyDto } from '@controlhome/api-client'
import { cn, useChartTheme } from '@controlhome/ui'
import { useElementSize, useMediaQuery } from '@vueuse/core'
import { computed, ref, type HTMLAttributes } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import {
  axisTimeFormatter,
  bucketEnergy,
  energyBucket,
  energyDigits,
  energyOption,
  energySlots,
  energyUnit,
  labelStep,
  type HistoryRange,
} from './chartOptions'
import './echarts'
import { useChartPalette } from './useChartPalette'

/**
 * Energy used as bars, per hour over a day or per day over longer ranges (from `EnergyHourly`, the one source of every
 * energy view): a bar for every hour (day) of the range up to the current one, which always has its label; labels stay
 * at least a finger apart at any width. Hours estimated from power readings are drawn lighter and say so in the tooltip.
 */
const props = defineProps<{
  rows: readonly EnergyHourlyDto[]
  range: HistoryRange
  /** The server's now: the last bar is the hour (day) it is in. */
  now: number
  label: string
  class?: HTMLAttributes['class']
}>()

const { t, locale } = useI18n()
const theme = useChartTheme()
const palette = useChartPalette('state-energy')
const box = ref<HTMLElement | null>(null)
const { width } = useElementSize(box)

const bucket = computed(() => energyBucket(props.range))
const buckets = computed(() => bucketEnergy(props.rows, bucket.value))
const slots = computed(() => energySlots(props.range, props.now))
const unit = computed(() => energyUnit(buckets.value))
const number = computed(
  () =>
    new Intl.NumberFormat(locale.value, {
      maximumFractionDigits: energyDigits(buckets.value, unit.value),
    }),
)
// Short labels, like the line charts': "8 AM" / "08:00", "14 Oct".
const formats = computed(() => {
  const axis = axisTimeFormatter(locale.value)
  return { hour: (time: number) => axis(time, false), day: (time: number) => axis(time, true) }
})

const reduceMotion = useMediaQuery('(prefers-reduced-motion: reduce)')

const option = computed(() => ({
  // Bars appear at once for people who asked for less motion (and for screenshots).
  animation: !reduceMotion.value,
  ...energyOption({
    buckets: buckets.value,
    slots: slots.value,
    bucket: bucket.value,
    palette: palette.value,
    unit: unit.value,
    step: labelStep(slots.value.length, width.value || 600),
    formatNumber: (value) => number.value.format(value),
    formatTime: (time, size) => formats.value[size](time),
    estimatedLabel: t('history.estimated'),
    name: props.label,
  }),
}))
</script>

<template>
  <div
    ref="box"
    :class="cn('relative h-52 w-full min-w-0 overflow-hidden', props.class)"
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
