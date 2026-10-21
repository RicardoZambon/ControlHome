<script setup lang="ts">
import type { HistorySeriesDto } from '@controlhome/api-client'
import { cn, useChartTheme } from '@controlhome/ui'
import { useMediaQuery } from '@vueuse/core'
import { computed, type HTMLAttributes } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { axisTimeFormatter, historyOption, type NoData, type TimeWindow } from './chartOptions'
import './echarts'
import { useChartPalette } from './useChartPalette'

/**
 * One attribute's history as a chart (vue-echarts, colors from the design tokens) over the window the whole History tab
 * shares: numbers as a line (raw changes as steps; 5-minute and hourly statistics as the mean over their min–max band)
 * that holds its last value and ends on the value now; enum states as steps between named levels. The part of the
 * window before the device started recording is shaded. Says so when the period has no history.
 */
const props = withDefaults(
  defineProps<{
    series: Pick<HistorySeriesDto, 'resolution' | 'points'>
    kind: 'number' | 'enum'
    window: TimeWindow
    /** Accessible name ("Power, last 24 hours"). */
    label: string
    /** The design token of the line. */
    token?: string
    /** Formats a value with its unit. */
    formatValue: (value: number) => string
    /** Names of enum values. */
    labelOf?: (value: string) => string
    enumValues?: readonly string[]
    /** The unit, named on the value axis. */
    unit?: string | null
    /** The value axis starts at zero (amounts such as watts and percentages). */
    zeroBased?: boolean
    /** The value now: the line ends on it. */
    live?: number | null
    /** The part of the window before the recording started. */
    noData?: NoData | null
    class?: HTMLAttributes['class']
  }>(),
  { token: 'primary', zeroBased: false, unit: null, live: null, noData: null },
)

const { t, locale } = useI18n()
const theme = useChartTheme()
const palette = useChartPalette(() => props.token)

const hasData = computed(() =>
  props.series.points.some((point) => point.v !== null || point.s !== null || point.min !== null),
)

const timeFormats = computed(() => ({
  time: new Intl.DateTimeFormat(locale.value, { hour: '2-digit', minute: '2-digit' }),
  dateTime: new Intl.DateTimeFormat(locale.value, {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }),
  axis: axisTimeFormatter(locale.value),
}))
const tick = computed(() => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1 }))

const reduceMotion = useMediaQuery('(prefers-reduced-motion: reduce)')

const option = computed(() => ({
  // Lines are drawn at once for people who asked for less motion (and for screenshots).
  animation: !reduceMotion.value,
  ...historyOption({
    series: props.series,
    kind: props.kind,
    window: props.window,
    palette: palette.value,
    formatValue: props.formatValue,
    formatTick: (value) => tick.value.format(value),
    formatTime: (time, withDate) =>
      withDate ? timeFormats.value.dateTime.format(time) : timeFormats.value.time.format(time),
    formatAxisTime: timeFormats.value.axis,
    labelOf: props.labelOf,
    enumValues: props.enumValues,
    name: props.label,
    unit: props.unit,
    zeroBased: props.zeroBased,
    live: props.live,
    noData: props.noData,
  }),
}))
</script>

<template>
  <div
    :class="cn('relative h-52 w-full min-w-0 overflow-hidden', props.class)"
    data-slot="history-chart"
    :data-points="series.points.length"
  >
    <VChart
      v-if="hasData"
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
      {{ t('history.empty') }}
    </div>
  </div>
</template>
