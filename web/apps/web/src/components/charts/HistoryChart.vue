<script setup lang="ts">
import type { HistorySeriesDto } from '@controlhome/api-client'
import { cn, useChartTheme } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { historyOption, type SeriesKind } from './chartOptions'
import './echarts'
import { useChartPalette } from './useChartPalette'

/**
 * One attribute's history as a chart (vue-echarts, colors from the design tokens): numbers as a line (raw changes as
 * steps held until the next change; 5-minute and hourly statistics as the mean over their min–max band), on/off states
 * as filled steps, enum states as steps between named levels. Says so when the period has no history.
 */
const props = withDefaults(
  defineProps<{
    series: Pick<HistorySeriesDto, 'resolution' | 'points'>
    kind: SeriesKind
    window: { min: number; max: number }
    /** Accessible name ("Power, last 24 hours"). */
    label: string
    /** The design token of the line. */
    token?: string
    /** Formats a value with its unit. */
    formatValue: (value: number) => string
    /** Names of on/off states (`'0'`, `'1'`) and enum values. */
    labelOf?: (value: string) => string
    enumValues?: readonly string[]
    class?: HTMLAttributes['class']
  }>(),
  { token: 'primary' },
)

const { t, locale } = useI18n()
const theme = useChartTheme()
const palette = useChartPalette(() => props.token)

const hasData = computed(() =>
  props.series.points.some((point) => point.v !== null || point.s !== null || point.min !== null),
)

const timeFormats = computed(() => ({
  time: new Intl.DateTimeFormat(locale.value, { hour: '2-digit', minute: '2-digit' }),
  date: new Intl.DateTimeFormat(locale.value, { day: 'numeric', month: 'short' }),
  dateTime: new Intl.DateTimeFormat(locale.value, {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }),
}))
const tick = computed(() => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1 }))

const option = computed(() =>
  historyOption({
    series: props.series,
    kind: props.kind,
    window: props.window,
    palette: palette.value,
    formatValue: props.formatValue,
    formatTick: (value) => tick.value.format(value),
    formatTime: (time, withDate) =>
      withDate ? timeFormats.value.date.format(time) : timeFormats.value.time.format(time),
    labelOf: props.labelOf,
    enumValues: props.enumValues,
    name: props.label,
  }),
)
</script>

<template>
  <div
    :class="cn('relative h-52 w-full', props.class)"
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
