import type { EnergyHourlyDto, HistoryPointDto, HistorySeriesDto } from '@controlhome/api-client'
import type { ChartOption, LineDataItem } from './echarts'

/**
 * ECharts options for history and energy (docs/architecture.md §6 "Reading history"), built from the API's series.
 * Pure: colors come in already resolved from the design tokens, and every rule is unit-tested.
 *
 * Every chart of a device's History tab shares one window — the chosen range up to the server's "now" — so cards side
 * by side cover the same hours, and the part of it before a device started recording is shaded and says so.
 */

/** The ranges the device page offers. */
export type HistoryRange = '24h' | '7d' | '30d'

export const HISTORY_RANGES: readonly HistoryRange[] = ['24h', '7d', '30d']

const HOUR_MS = 3_600_000
const DAY_MS = 24 * HOUR_MS
const RANGE_MS: Record<HistoryRange, number> = { '24h': DAY_MS, '7d': 7 * DAY_MS, '30d': 30 * DAY_MS }

export interface TimeWindow {
  min: number
  max: number
}

export function isHistoryRange(value: unknown): value is HistoryRange {
  return typeof value === 'string' && (HISTORY_RANGES as readonly string[]).includes(value)
}

/** How long a range is, in ms. */
export function rangeMs(range: HistoryRange): number {
  return RANGE_MS[range]
}

/** The window of every chart of the tab: the range up to `end` (the server's now). */
export function historyWindow(range: HistoryRange, end: number): TimeWindow {
  return { min: end - RANGE_MS[range], max: end }
}

/** The query of a window: its exact start and end, so every card asks for the same period. */
export function windowQuery(window: TimeWindow): { from: string; to: string } {
  return { from: new Date(window.min).toISOString(), to: new Date(window.max).toISOString() }
}

/** Energy is shown per hour over a day, per day over a week or a month. */
export function energyBucket(range: HistoryRange): 'hour' | 'day' {
  return range === '24h' ? 'hour' : 'day'
}

/**
 * Where the history starts inside the window when that is after the window starts (a device added an hour ago): the
 * part before it has no data. `null` when the history reaches back to the window's start, or there is none.
 */
export function recordingStart(window: TimeWindow, oldest: number | undefined): number | null {
  if (oldest === undefined) return null
  const slack = (window.max - window.min) * 0.01
  return oldest > window.min + slack ? Math.min(oldest, window.max) : null
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
  /** The shade over the part of the window without data. */
  shade: string
  /** Text on that shade. */
  shadeText: string
}

/** The part of the window before the history starts, and what it says ("Recording since 06:40"). */
export interface NoData {
  until: number
  label: string
}

export interface HistoryOptionInput {
  series: Pick<HistorySeriesDto, 'resolution' | 'points'>
  kind: 'number' | 'enum'
  window: TimeWindow
  palette: ChartPalette
  /** Formats a number in the tooltip (with its unit). */
  formatValue: (value: number) => string
  /** Formats a number on the axis (without the unit, which names the axis). */
  formatTick: (value: number) => string
  /** Formats a time in the tooltip. */
  formatTime: (time: number, withDate: boolean) => string
  /** Formats a time on the axis (short: "8 PM", "20:00", "Oct 14"); defaults to `formatTime`. */
  formatAxisTime?: (time: number, withDate: boolean) => string
  /** Names of `enum` values. */
  labelOf?: (value: string) => string
  /** The values an `enum` can take, in order (else the ones seen). */
  enumValues?: readonly string[]
  /** Accessible series name. */
  name: string
  /** The unit, named at the top of the value axis ("W"). */
  unit?: string | null
  /** The value axis starts at zero (amounts: watts, percentages); otherwise it fits the values (temperatures). */
  zeroBased?: boolean
  /** The value now (live): the line ends on it, at the window's end, like the card's headline. */
  live?: number | null
  /** The part of the window before the history starts: shaded, and labelled when it is wide enough. */
  noData?: NoData | null
}

/** A point's time in ms. */
const timeOf = (point: Pick<HistoryPointDto, 't'>) => Date.parse(point.t)

/**
 * A value holds until the next change: the line runs on to the end of the window with the last value (a step line),
 * never ends at the last change.
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
 * Time axis ticks: between four and twelve across the window at any width (labels that would overlap are dropped), and
 * none on the window's edges, where a label would be cut in half.
 */
function timeAxis(window: TimeWindow, format: (time: number, withDate: boolean) => string) {
  const span = window.max - window.min
  const withDate = span > DAY_MS
  return {
    type: 'time' as const,
    min: window.min,
    max: window.max,
    minInterval: span / 12,
    maxInterval: span / 4,
    splitLine: { show: false },
    axisTick: { show: false },
    axisLabel: {
      hideOverlap: true,
      showMinLabel: false,
      showMaxLabel: false,
      formatter: (value: number) => format(value, withDate),
    },
  }
}

/** The no-data shade as a mark area of the first series (labelled when it spans at least 30 % of the window). */
function noDataArea(noData: NoData | null | undefined, window: TimeWindow, palette: ChartPalette) {
  if (!noData || noData.until <= window.min) return undefined
  const wide = (noData.until - window.min) / (window.max - window.min) >= 0.3
  return {
    silent: true,
    itemStyle: { color: palette.shade },
    label: {
      show: wide,
      position: 'inside' as const,
      color: palette.shadeText,
      fontSize: 12,
      formatter: noData.label,
    },
    data: [[{ xAxis: window.min }, { xAxis: noData.until }] as [{ xAxis: number }, { xAxis: number }]],
  }
}

/** The last point of a line: marked with a dot (it is "now", the headline's value). */
function nowPoint(time: number, value: number | string, color: string) {
  return { value: [time, value], symbol: 'circle', symbolSize: 7, itemStyle: { color } }
}

export function historyOption(input: HistoryOptionInput): ChartOption {
  const { series, kind, window, palette } = input
  const raw = series.resolution === 'raw'
  const withDate = window.max - window.min > DAY_MS
  const xAxis = timeAxis(window, input.formatAxisTime ?? input.formatTime)
  const grid = { left: 4, right: 16, top: input.unit ? 26 : 12, bottom: 4, containLabel: true }
  const shade = noDataArea(input.noData, window, palette)

  if (kind === 'enum') {
    // A single reading (a device that just started) is a dot where it was taken, then its value held to now.
    const showSymbol = series.points.length <= 1
    const categories = enumCategories(series.points, input.enumValues)
    const data: LineDataItem[] = extendToEnd<string | null>(
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
          markArea: shade,
        },
      ],
    }
  }

  // Numbers: raw changes as a step line; statistics as the mean over its min–max band. Either way the last value holds
  // to the window's end and the line ends on the value now.
  const values: [number, number | null][] = series.points.map((point) => [timeOf(point), point.v])
  const last = values[values.length - 1]
  const extended: [number, number | null][] = [...values]
  // Held to now unless the last reading says the value is unknown (the device stopped reporting it).
  if (last && last[1] !== null && last[0] < window.max) {
    const end = input.live ?? last[1]
    // A raw value holds until it changes: the step runs on at the last value, then ends on the value now.
    if (raw && end !== last[1]) extended.push([window.max, last[1]])
    extended.push([window.max, end])
  }
  const lineData: LineDataItem[] = extended.map(([time, value], index) => {
    if (value !== null && index === extended.length - 1 && time === window.max)
      return nowPoint(time, value, palette.line)
    if (value !== null && index === 0 && values.length === 1)
      return { value: [time, value], symbol: 'circle', symbolSize: 6 }
    return [time, value]
  })

  // The band follows the line point for point (a raw change or "now" is its own min and max), so they never part.
  const banded = !raw && series.points.some((point) => point.min !== null && point.max !== null)
  const bounds = extended.map(([time, value], index) => {
    const point = series.points[index]
    const min = point?.min ?? value
    const max = point?.max ?? value
    return { time, min, range: min !== null && max !== null ? max - min : null }
  })
  const lines: ChartOption['series'] = []
  if (banded) {
    lines.push(
      {
        type: 'line',
        name: 'min',
        data: bounds.map((entry) => [entry.time, entry.min]),
        stack: 'band',
        showSymbol: false,
        silent: true,
        lineStyle: { opacity: 0 },
        tooltip: { show: false },
        markArea: shade,
      },
      {
        type: 'line',
        name: 'range',
        data: bounds.map((entry) => [entry.time, entry.range]),
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
    data: lineData,
    step: raw ? 'end' : false,
    // Dots only where an item asks for one: "now", and a lone reading.
    showSymbol: true,
    symbol: 'none',
    connectNulls: false,
    lineStyle: { width: 2, color: palette.line },
    itemStyle: { color: palette.line },
    areaStyle: banded ? undefined : area(palette),
    markArea: banded ? undefined : shade,
    z: 3,
  })
  return {
    grid,
    xAxis,
    yAxis: {
      type: 'value',
      scale: !input.zeroBased,
      min: input.zeroBased ? 0 : undefined,
      name: input.unit ?? undefined,
      nameLocation: 'end',
      nameGap: 10,
      nameTextStyle: { align: 'right', fontSize: 11 },
      splitLine: { lineStyle: { type: 'dashed' } },
      axisLabel: { formatter: (value: number) => input.formatTick(value) },
    },
    tooltip: {
      trigger: 'axis',
      // Inside the chart: never past a phone's edge, never cut off by the card.
      confine: true,
      formatter: (params: unknown) => tooltipText(params, input, withDate),
    },
    series: lines,
  }
}

function area(palette: ChartPalette) {
  return {
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

// --- On/off timeline ---------------------------------------------------------------------------------------------------

export interface StateSegment {
  start: number
  end: number
  /** `null`: nothing recorded yet. */
  on: boolean | null
}

/**
 * An on/off history as segments covering the window: before the first reading nothing is known; each reading holds
 * until the next one, the last one until the window's end. Readings outside the window are clipped to it.
 */
export function stateSegments(
  points: readonly Pick<HistoryPointDto, 't' | 'v'>[],
  window: TimeWindow,
): StateSegment[] {
  const segments: StateSegment[] = []
  const clamp = (time: number) => Math.min(window.max, Math.max(window.min, time))
  const first = points[0]
  if (!first || clamp(timeOf(first)) > window.min) {
    segments.push({ start: window.min, end: first ? clamp(timeOf(first)) : window.max, on: null })
  }
  points.forEach((point, index) => {
    const start = clamp(timeOf(point))
    const next = points[index + 1]
    const end = next ? clamp(timeOf(next)) : window.max
    if (end <= start) return
    const on = point.v === null ? null : point.v > 0
    const previous = segments[segments.length - 1]
    if (previous && previous.on === on && previous.end === start) previous.end = end
    else segments.push({ start, end, on })
  })
  return segments
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

/** The start of the hour a time is in. */
function startOfHour(time: number): number {
  return Math.floor(time / HOUR_MS) * HOUR_MS
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

/**
 * The buckets a range's bars stand in, oldest first, ending with the one "now" is in: the 24 hours of a day, the 7 or 30
 * days of a week or a month (local days).
 */
export function energySlots(range: HistoryRange, now: number): number[] {
  if (energyBucket(range) === 'hour') {
    const current = startOfHour(now)
    return Array.from({ length: 24 }, (_, index) => current - (23 - index) * HOUR_MS)
  }
  const days = range === '7d' ? 7 : 30
  const slots: number[] = []
  const date = new Date(startOfDay(now))
  for (let index = 0; index < days; index++) {
    slots.unshift(date.getTime())
    date.setDate(date.getDate() - 1)
  }
  return slots
}

/** Watt-hours in total. */
export function totalWh(rows: readonly Pick<EnergyHourlyDto, 'wh'>[]): number {
  return rows.reduce((sum, row) => sum + row.wh, 0)
}

/** kWh once any bucket reaches 1 kWh, else Wh. */
export function energyUnit(buckets: readonly Pick<EnergyBucket, 'wh'>[]): 'Wh' | 'kWh' {
  return buckets.some((entry) => entry.wh >= 1000) ? 'kWh' : 'Wh'
}

/**
 * Fraction digits of the energy axis and tooltips: kWh to two decimals; Wh whole from 10 Wh, and finer below so a
 * device that has used less than a watt-hour gets "0.25" ticks rather than a column of "0".
 */
export function energyDigits(buckets: readonly Pick<EnergyBucket, 'wh'>[], unit: 'Wh' | 'kWh'): number {
  if (unit === 'kWh') return 2
  const most = Math.max(0, ...buckets.map((entry) => entry.wh))
  if (most === 0 || most >= 10) return 0
  return most >= 1 ? 1 : 2
}

/**
 * Every how many slots a label is drawn so labels are at least `minLabelPx` apart on a chart `widthPx` wide (at least
 * every slot, and never fewer than four labels when the slots allow).
 */
export function labelStep(slots: number, widthPx: number, minLabelPx = 64): number {
  const fit = Math.max(4, Math.floor(widthPx / minLabelPx))
  return Math.max(1, Math.ceil(slots / fit))
}

export interface EnergyOptionInput {
  buckets: readonly EnergyBucket[]
  /** The slots shown (`energySlots`): a bar for each, empty when nothing was used. */
  slots: readonly number[]
  bucket: 'hour' | 'day'
  palette: ChartPalette
  unit: 'Wh' | 'kWh'
  /** A label every this many slots, counted back from the current one (which always has its label). */
  step: number
  formatNumber: (value: number) => string
  formatTime: (time: number, bucket: 'hour' | 'day') => string
  /** "estimated" in the UI language. */
  estimatedLabel: string
  name: string
}

export function energyOption(input: EnergyOptionInput): ChartOption {
  const byStart = new Map(input.buckets.map((entry) => [entry.start, entry]))
  const last = input.slots.length - 1
  const data = input.slots.map((start) => {
    const entry = byStart.get(start)
    const wh = entry?.wh ?? 0
    return {
      value: input.unit === 'kWh' ? wh / 1000 : wh,
      estimated: entry?.estimated ?? false,
      start,
      itemStyle: {
        color: entry?.estimated ? input.palette.estimated : input.palette.line,
        borderRadius: [3, 3, 0, 0],
      },
    }
  })
  const labelled = (index: number) => (last - index) % input.step === 0
  return {
    grid: { left: 4, right: 12, top: 26, bottom: 4, containLabel: true },
    xAxis: {
      type: 'category',
      data: input.slots.map(String),
      axisTick: { show: true, alignWithLabel: true, interval: labelled },
      axisLabel: {
        interval: labelled,
        formatter: (value: string) => input.formatTime(Number(value), input.bucket),
      },
    },
    yAxis: {
      type: 'value',
      min: 0,
      name: input.unit,
      nameLocation: 'end',
      nameGap: 10,
      nameTextStyle: { align: 'right', fontSize: 11 },
      splitLine: { lineStyle: { type: 'dashed' } },
      axisLabel: { formatter: (value: number) => input.formatNumber(value) },
    },
    tooltip: {
      trigger: 'axis',
      confine: true,
      axisPointer: { type: 'shadow' },
      formatter: (params: unknown) => {
        const [first] = (Array.isArray(params) ? params : [params]) as {
          data?: { value: number; estimated?: boolean; start: number }
        }[]
        if (!first?.data) return ''
        const amount = `${input.formatNumber(first.data.value)} ${input.unit}`
        const note = first.data.estimated
          ? ` <span style="opacity:.7">(${escapeHtml(input.estimatedLabel)})</span>`
          : ''
        return `${escapeHtml(input.formatTime(first.data.start, input.bucket))}<br/><strong>${escapeHtml(amount)}</strong>${note}`
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

/** A short time for an axis: "8 PM" / "20:00" on the hour, "8:30 PM" / "20:30" otherwise, "14 Oct" with dates. */
export function axisTimeFormatter(locale: string): (time: number, withDate: boolean) => string {
  const twelveHour = ['h11', 'h12'].includes(
    new Intl.DateTimeFormat(locale, { hour: 'numeric' }).resolvedOptions().hourCycle ?? '',
  )
  const hour = new Intl.DateTimeFormat(locale, { hour: 'numeric' })
  const time = new Intl.DateTimeFormat(locale, {
    hour: twelveHour ? 'numeric' : '2-digit',
    minute: '2-digit',
  })
  const date = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short' })
  return (value, withDate) => {
    if (withDate) return date.format(value)
    const onTheHour = new Date(value).getMinutes() === 0
    return twelveHour && onTheHour ? hour.format(value) : time.format(value)
  }
}
