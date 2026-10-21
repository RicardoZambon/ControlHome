import type { EnergyHourlyDto, HistoryPointDto, HistorySeriesDto } from '@controlhome/api-client'
import type { ChartOption } from './echarts'

/**
 * ECharts options for history and energy (docs/architecture.md §6 "Reading history"), built from the API's series.
 * Pure: colors come in already resolved from the design tokens, and every rule is unit-tested.
 */

/** The ranges the device page offers. */
export type HistoryRange = '24h' | '7d' | '30d'

export const HISTORY_RANGES: readonly HistoryRange[] = ['24h', '7d', '30d']

const HOUR_MS = 3_600_000
const DAY_MS = 24 * HOUR_MS
const RANGE_MS: Record<HistoryRange, number> = { '24h': DAY_MS, '7d': 7 * DAY_MS, '30d': 30 * DAY_MS }

export function isHistoryRange(value: unknown): value is HistoryRange {
  return typeof value === 'string' && (HISTORY_RANGES as readonly string[]).includes(value)
}

/** How long a range is, in ms. */
export function rangeMs(range: HistoryRange): number {
  return RANGE_MS[range]
}

/**
 * The query of a range. The last 24 h is the server's own default (its clock, which tests move forward); longer ranges
 * start that long before now and end at the server's now.
 */
export function rangeQuery(range: HistoryRange, now: number): { from?: string } {
  return range === '24h' ? {} : { from: new Date(now - RANGE_MS[range]).toISOString() }
}

/** Energy is shown per hour over a day, per day over a week or a month. */
export function energyBucket(range: HistoryRange): 'hour' | 'day' {
  return range === '24h' ? 'hour' : 'day'
}

/** The x-axis window: the range up to now, stretched to the newest point (the server's clock may be ahead). */
export function timeWindow(range: HistoryRange, now: number, newest?: number): { min: number; max: number } {
  const max = Math.max(now, newest ?? now)
  return { min: max - RANGE_MS[range], max }
}

/** The narrowest window `fitWindow` shows. */
const MIN_FIT_MS = 10 * 60_000

/**
 * The window narrowed to a history that starts inside it (a device added an hour ago has nothing before): from a
 * little before the first point, at least ten minutes wide, never wider than the window. Otherwise the whole window —
 * a recent history would be a sliver at its right edge.
 */
export function fitWindow(
  window: { min: number; max: number },
  oldest: number | undefined,
): { min: number; max: number } {
  if (oldest === undefined || oldest <= window.min) return window
  const span = window.max - oldest
  const min = Math.min(oldest - span * 0.1, window.max - MIN_FIT_MS)
  return { min: Math.max(window.min, min), max: window.max }
}

/** What kind of value a series carries, which decides how it is drawn. */
export type SeriesKind = 'number' | 'bool' | 'enum'

export interface ChartPalette {
  /** The line or bar color. */
  line: string
  /** The fill under a line, at its top. */
  area: string
  /** The fill under a line, at its bottom (usually transparent). */
  areaFade: string
  /** The min–max band of statistics. */
  band: string
  /** Estimated energy (integrated from power readings). */
  estimated: string
}

export interface HistoryOptionInput {
  series: Pick<HistorySeriesDto, 'resolution' | 'points'>
  kind: SeriesKind
  window: { min: number; max: number }
  palette: ChartPalette
  /** Formats a number on the axis and in the tooltip (with its unit). */
  formatValue: (value: number) => string
  /** Formats a number on the axis (without the unit). */
  formatTick: (value: number) => string
  /** Formats a time on the axis and in the tooltip. */
  formatTime: (time: number, withDate: boolean) => string
  /** Names of `bool` states (0, 1) and `enum` values. */
  labelOf?: (value: string) => string
  /** The values an `enum` can take, in order (else the ones seen). */
  enumValues?: readonly string[]
  /** Accessible series name. */
  name: string
  /**
   * The last value holds until the window's end: always for raw changes, and for statistics followed by the recent
   * raw changes (`withRawTail`).
   */
  heldToEnd?: boolean
}

/** A point's time in ms. */
const timeOf = (point: Pick<HistoryPointDto, 't'>) => Date.parse(point.t)

/**
 * Raw history holds a value until the next change: the line runs on to the end of the window with the last value
 * (a step line), never ends at the last change.
 */
export function extendToEnd<T>(points: readonly [number, T][], end: number): [number, T][] {
  const last = points[points.length - 1]
  if (!last || last[0] >= end || last[1] === null) return [...points]
  return [...points, [end, last[1]]]
}

/** How long a statistics bucket lasts. */
const BUCKET_MS: Record<string, number> = { '5m': 5 * 60_000, '1h': HOUR_MS }

/** The longest raw stretch the server serves for `auto` (docs/architecture.md §6 "Reading history"). */
const RAW_TAIL_MAX_MS = 6 * HOUR_MS

/**
 * Statistics end where the last closed bucket ends (up to a few minutes ago, a fresh device has none yet): where the
 * recent raw changes that complete such a series start — the last bucket's end, or the last 6 hours when there is no
 * bucket. `null` for a raw series (it is complete).
 */
export function rawTailFrom(
  series: Pick<HistorySeriesDto, 'resolution' | 'points'>,
  now: number,
): string | null {
  const bucket = BUCKET_MS[series.resolution]
  if (bucket === undefined) return null
  const last = series.points[series.points.length - 1]
  const from = last ? timeOf(last) + bucket : now - RAW_TAIL_MAX_MS
  return new Date(from).toISOString()
}

/**
 * Statistics completed with the raw changes after their last bucket (as the newest part of a chart: what happened in
 * the last minutes, before its bucket closed). The raw series is the one served from `rawTailFrom`.
 */
export function withRawTail<T extends Pick<HistorySeriesDto, 'resolution' | 'points'>>(
  statistics: T,
  raw: Pick<HistorySeriesDto, 'points'>,
): T {
  const last = statistics.points[statistics.points.length - 1]
  const bucket = BUCKET_MS[statistics.resolution] ?? 0
  const end = last ? timeOf(last) + bucket : -Infinity
  const tail = raw.points.filter((point) => timeOf(point) >= end)
  return { ...statistics, points: [...statistics.points, ...tail] }
}

/** The distinct string values of a series, in the order given (then any others in the order seen). */
export function enumCategories(
  points: readonly Pick<HistoryPointDto, 's'>[],
  known: readonly string[] = [],
): string[] {
  const seen = [...known]
  for (const point of points) if (point.s !== null && !seen.includes(point.s)) seen.push(point.s)
  return seen
}

/**
 * A time label on the window's first or last instant is centred on the chart's edge, so half of it was cut off ("06:17
 * A"): the first one is aligned to start there and the last one to end there.
 */
const EDGE_LABELS = { alignMinLabel: 'left', alignMaxLabel: 'right' } as const

export function historyOption(input: HistoryOptionInput): ChartOption {
  const { series, kind, window, palette } = input
  const raw = series.resolution === 'raw'
  const held = raw || input.heldToEnd === true
  // A series of one or two points (a device that just started reporting) can be a sliver at the window's edge: its
  // points are marked so it still shows.
  const showSymbol = series.points.length <= 2
  const withDate = window.max - window.min > DAY_MS
  const xAxis = {
    type: 'time' as const,
    min: window.min,
    max: window.max,
    splitLine: { show: false },
    axisTick: { show: false },
    axisLabel: {
      ...EDGE_LABELS,
      hideOverlap: true,
      formatter: (value: number) => input.formatTime(value, withDate),
    },
  }
  const grid = { left: 4, right: 12, top: 12, bottom: 4, containLabel: true }
  const area = {
    color: {
      type: 'linear' as const,
      x: 0,
      y: 0,
      x2: 0,
      y2: 1,
      colorStops: [
        { offset: 0, color: palette.area },
        { offset: 1, color: palette.areaFade },
      ],
    },
  }

  if (kind === 'enum') {
    const categories = enumCategories(series.points, input.enumValues)
    const data = extendToEnd<string | null>(
      series.points.map((point) => [timeOf(point), point.s]),
      window.max,
    )
    return {
      grid,
      xAxis,
      yAxis: {
        type: 'category',
        data: categories,
        axisTick: { show: false },
        axisLabel: { formatter: (value: string) => input.labelOf?.(value) ?? value },
      },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => (typeof value === 'string' ? (input.labelOf?.(value) ?? value) : '—'),
      },
      series: [
        {
          type: 'line',
          name: input.name,
          data,
          step: 'end',
          showSymbol,
          connectNulls: false,
          lineStyle: { width: 2, color: palette.line },
          itemStyle: { color: palette.line },
        },
      ],
    }
  }

  if (kind === 'bool') {
    const data = extendToEnd<number | null>(
      series.points.map((point) => [timeOf(point), point.v === null ? null : point.v > 0 ? 1 : 0]),
      window.max,
    )
    return {
      grid,
      xAxis,
      yAxis: {
        type: 'value',
        min: 0,
        max: 1,
        interval: 1,
        axisLabel: { formatter: (value: number) => input.labelOf?.(String(value)) ?? String(value) },
      },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) =>
          typeof value === 'number' ? (input.labelOf?.(String(value)) ?? String(value)) : '—',
      },
      series: [
        {
          type: 'line',
          name: input.name,
          data,
          step: 'end',
          showSymbol,
          connectNulls: false,
          lineStyle: { width: 2, color: palette.line },
          itemStyle: { color: palette.line },
          areaStyle: area,
        },
      ],
    }
  }

  // Numbers: raw changes as a step line; statistics as the mean, with the min–max band behind it.
  const values = series.points.map((point): [number, number | null] => [timeOf(point), point.v])
  const banded = !raw && series.points.some((point) => point.min !== null && point.max !== null)
  const lines: ChartOption['series'] = []
  if (banded) {
    lines.push(
      {
        type: 'line',
        name: 'min',
        data: series.points.map((point) => [timeOf(point), point.min]),
        stack: 'band',
        showSymbol: false,
        silent: true,
        lineStyle: { opacity: 0 },
        tooltip: { show: false },
      },
      {
        type: 'line',
        name: 'range',
        data: series.points.map((point) => [
          timeOf(point),
          point.min !== null && point.max !== null ? point.max - point.min : null,
        ]),
        stack: 'band',
        showSymbol: false,
        silent: true,
        lineStyle: { opacity: 0 },
        areaStyle: { color: palette.band },
        tooltip: { show: false },
      },
    )
  }
  lines.push({
    type: 'line',
    name: input.name,
    data: held ? extendToEnd(values, window.max) : values,
    step: raw ? 'end' : false,
    showSymbol,
    connectNulls: false,
    lineStyle: { width: 2, color: palette.line },
    itemStyle: { color: palette.line },
    areaStyle: banded ? undefined : area,
    z: 3,
  })
  return {
    grid,
    xAxis,
    yAxis: {
      type: 'value',
      scale: true,
      splitLine: { lineStyle: { type: 'dashed' } },
      axisLabel: { formatter: (value: number) => input.formatTick(value) },
    },
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => tooltipText(params, input, withDate),
    },
    series: lines,
  }
}

interface TooltipParam {
  seriesName?: string
  value?: unknown
}

function tooltipText(params: unknown, input: HistoryOptionInput, withDate: boolean): string {
  const list = (Array.isArray(params) ? params : [params]) as TooltipParam[]
  const main = list.find((entry) => entry.seriesName === input.name)
  const value = Array.isArray(main?.value) ? (main.value as [number, number | null]) : undefined
  if (!value) return ''
  const time = escapeHtml(input.formatTime(value[0], withDate))
  const reading = value[1] === null ? '—' : escapeHtml(input.formatValue(value[1]))
  return `${time}<br/><strong>${reading}</strong>`
}

/** Tooltip text is HTML: names and values are escaped. */
export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (char) => `&#${char.charCodeAt(0)};`)
}

// --- Energy ------------------------------------------------------------------------------------------------------------

export interface EnergyBucket {
  /** The bucket's start (ms, local time for days). */
  start: number
  wh: number
  /** Any of its hours was estimated from power readings. */
  estimated: boolean
}

/** Local midnight of a time (the browser's time zone, like the chart's axis). */
function startOfDay(time: number): number {
  const date = new Date(time)
  date.setHours(0, 0, 0, 0)
  return date.getTime()
}

/** Hourly energy summed per hour (all endpoints) or per local day, oldest first. */
export function bucketEnergy(
  rows: readonly Pick<EnergyHourlyDto, 'hourStartUtc' | 'wh' | 'estimated'>[],
  bucket: 'hour' | 'day',
): EnergyBucket[] {
  const buckets = new Map<number, EnergyBucket>()
  for (const row of rows) {
    const time = Date.parse(row.hourStartUtc)
    if (!Number.isFinite(time)) continue
    const start = bucket === 'hour' ? time : startOfDay(time)
    const current = buckets.get(start) ?? { start, wh: 0, estimated: false }
    buckets.set(start, { start, wh: current.wh + row.wh, estimated: current.estimated || row.estimated })
  }
  return [...buckets.values()].sort((a, b) => a.start - b.start)
}

/** Watt-hours in total. */
export function totalWh(rows: readonly Pick<EnergyHourlyDto, 'wh'>[]): number {
  return rows.reduce((sum, row) => sum + row.wh, 0)
}

/** kWh once any bucket reaches 1 kWh, else Wh. */
export function energyUnit(buckets: readonly Pick<EnergyBucket, 'wh'>[]): 'Wh' | 'kWh' {
  return buckets.some((entry) => entry.wh >= 1000) ? 'kWh' : 'Wh'
}

export interface EnergyOptionInput {
  buckets: readonly EnergyBucket[]
  bucket: 'hour' | 'day'
  window: { min: number; max: number }
  palette: ChartPalette
  unit: 'Wh' | 'kWh'
  formatNumber: (value: number) => string
  formatTime: (time: number, bucket: 'hour' | 'day') => string
  /** "estimated" in the UI language. */
  estimatedLabel: string
  name: string
}

export function energyOption(input: EnergyOptionInput): ChartOption {
  const size = input.bucket === 'hour' ? HOUR_MS : DAY_MS
  const data = input.buckets.map((entry) => ({
    value: [entry.start + size / 2, input.unit === 'kWh' ? entry.wh / 1000 : entry.wh] as [number, number],
    estimated: entry.estimated,
    itemStyle: {
      color: entry.estimated ? input.palette.estimated : input.palette.line,
      borderRadius: [3, 3, 0, 0],
    },
  }))
  return {
    grid: { left: 4, right: 12, top: 12, bottom: 4, containLabel: true },
    xAxis: {
      type: 'time',
      min: input.bucket === 'hour' ? input.window.min : startOfDay(input.window.min),
      max: input.bucket === 'hour' ? input.window.max : startOfDay(input.window.max) + DAY_MS,
      splitLine: { show: false },
      axisTick: { show: false },
      axisLabel: {
        ...EDGE_LABELS,
        hideOverlap: true,
        formatter: (value: number) => input.formatTime(value, input.bucket === 'hour' ? 'hour' : 'day'),
      },
    },
    yAxis: {
      type: 'value',
      min: 0,
      splitLine: { lineStyle: { type: 'dashed' } },
      axisLabel: { formatter: (value: number) => input.formatNumber(value) },
    },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (params: unknown) => {
        const [first] = (Array.isArray(params) ? params : [params]) as {
          value?: [number, number]
          data?: { estimated?: boolean }
        }[]
        if (!first?.value) return ''
        const start = first.value[0] - size / 2
        const amount = `${input.formatNumber(first.value[1])} ${input.unit}`
        const note = first.data?.estimated
          ? ` <span style="opacity:.7">(${escapeHtml(input.estimatedLabel)})</span>`
          : ''
        return `${escapeHtml(input.formatTime(start, input.bucket))}<br/><strong>${escapeHtml(amount)}</strong>${note}`
      },
    },
    series: [
      {
        type: 'bar',
        name: input.name,
        data,
        barMaxWidth: input.bucket === 'hour' ? 14 : 28,
        barCategoryGap: '30%',
      },
    ],
  }
}
