import type { HistoryPointDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import {
  axisTimeFormatter,
  bucketEnergy,
  energyBucket,
  energyDigits,
  energyOption,
  energySlots,
  energyUnit,
  enumCategories,
  escapeHtml,
  extendToEnd,
  historyOption,
  historyWindow,
  isHistoryRange,
  labelStep,
  rawTailFrom,
  recordingStart,
  stateSegments,
  totalWh,
  windowQuery,
  withRawTail,
  type ChartPalette,
  type HistoryOptionInput,
} from './chartOptions'

// Placeholder names: the real colors come from the design tokens.
const PALETTE: ChartPalette = {
  line: 'palette-line',
  area: 'palette-area',
  areaFade: 'palette-fade',
  band: 'palette-band',
  estimated: 'palette-estimated',
  shade: 'palette-shade',
  shadeText: 'palette-shade-text',
}

const NOW = Date.parse('2026-10-21T12:00:00Z')
const HOUR = 3_600_000
const MINUTE = 60_000
const DAY_WINDOW = { min: NOW - 24 * HOUR, max: NOW }

function point(t: number, v: number | null, extra: Partial<HistoryPointDto> = {}): HistoryPointDto {
  return { t: new Date(t).toISOString(), v, min: null, max: null, s: null, ...extra }
}

function input(extra: Partial<HistoryOptionInput>): HistoryOptionInput {
  return {
    series: { resolution: 'raw', points: [] },
    kind: 'number',
    window: DAY_WINDOW,
    palette: PALETTE,
    formatValue: (value) => `${value} W`,
    formatTick: (value) => String(value),
    formatTime: (time) => new Date(time).toISOString(),
    name: 'Power',
    ...extra,
  }
}

type SeriesList = {
  name?: string
  data: unknown[]
  step?: unknown
  stack?: string
  areaStyle?: unknown
  markArea?: { data: unknown; label: { show: boolean; formatter: string } }
}[]

/** A line's data as [time, value] pairs (items with their own symbol unwrapped). */
const pairs = (data: unknown[]): [number, unknown][] =>
  data.map(
    (entry) => (Array.isArray(entry) ? entry : (entry as { value: unknown }).value) as [number, unknown],
  )

describe('ranges and windows', () => {
  it('knows the ranges', () => {
    expect(isHistoryRange('7d')).toBe(true)
    expect(isHistoryRange('1y')).toBe(false)
    expect(energyBucket('24h')).toBe('hour')
    expect(energyBucket('30d')).toBe('day')
  })

  it('gives every card the same window: the range up to the server now, asked for exactly', () => {
    expect(historyWindow('24h', NOW)).toEqual(DAY_WINDOW)
    expect(historyWindow('7d', NOW).min).toBe(NOW - 7 * 24 * HOUR)
    expect(windowQuery(DAY_WINDOW)).toEqual({
      from: '2026-10-20T12:00:00.000Z',
      to: '2026-10-21T12:00:00.000Z',
    })
  })

  it('finds where a recording starts inside the window', () => {
    expect(recordingStart(DAY_WINDOW, NOW - 5 * HOUR)).toBe(NOW - 5 * HOUR)
    // From the start of the window (the value in effect then), or nothing: no gap.
    expect(recordingStart(DAY_WINDOW, NOW - 24 * HOUR)).toBeNull()
    expect(recordingStart(DAY_WINDOW, NOW - 23.9 * HOUR)).toBeNull()
    expect(recordingStart(DAY_WINDOW, undefined)).toBeNull()
  })
})

describe('historyOption', () => {
  it('draws raw numbers as a step line held to now, ending on the live value', () => {
    const option = historyOption(
      input({
        series: { resolution: 'raw', points: [point(NOW - 3 * HOUR, 10), point(NOW - HOUR, 25)] },
        live: 27,
      }),
    )
    const [line] = option.series as SeriesList
    expect(line!.step).toBe('end')
    expect(pairs(line!.data)).toEqual([
      [NOW - 3 * HOUR, 10],
      [NOW - HOUR, 25],
      [NOW, 25],
      [NOW, 27],
    ])
    // "Now" is marked, like the headline.
    expect(line!.data.at(-1)).toMatchObject({ symbol: 'circle' })
  })

  it('turns a single reading into a flat line from it to now', () => {
    const option = historyOption(
      input({ series: { resolution: '5m', points: [point(NOW - 40 * MINUTE, 49)] } }),
    )
    const [line] = option.series as SeriesList
    expect(pairs(line!.data)).toEqual([
      [NOW - 40 * MINUTE, 49],
      [NOW, 49],
    ])
    expect(line!.data[0]).toMatchObject({ symbol: 'circle' })
  })

  it('does not hold a value the device stopped reporting', () => {
    const option = historyOption(
      input({ series: { resolution: 'raw', points: [point(NOW - 2 * HOUR, 5), point(NOW - HOUR, null)] } }),
    )
    expect(pairs((option.series as SeriesList)[0]!.data)).toHaveLength(2)
  })

  it('draws statistics as the mean over a band that follows the line to now', () => {
    const option = historyOption(
      input({
        series: {
          resolution: '5m',
          points: [
            point(NOW - 10 * MINUTE, 20, { min: 18, max: 23 }),
            point(NOW - 5 * MINUTE, 21, { min: 20, max: 22 }),
            // A raw change after the last closed bucket: its own min and max.
            point(NOW - 2 * MINUTE, 26),
          ],
        },
        live: 25,
      }),
    )
    const series = option.series as SeriesList
    expect(series.map((entry) => entry.name)).toEqual(['min', 'range', 'Power'])
    expect(series[0]!.data).toEqual([
      [NOW - 10 * MINUTE, 18],
      [NOW - 5 * MINUTE, 20],
      [NOW - 2 * MINUTE, 26],
      [NOW, 25],
    ])
    expect(series[1]!.data).toEqual([
      [NOW - 10 * MINUTE, 5],
      [NOW - 5 * MINUTE, 2],
      [NOW - 2 * MINUTE, 0],
      [NOW, 0],
    ])
    expect(series[1]!.stack).toBe('band')
    expect(series[2]!.step).toBe(false)
    expect(pairs(series[2]!.data).map(([time]) => time)).toEqual(
      series[0]!.data.map((entry) => (entry as number[])[0]),
    )
  })

  it('names the unit on the value axis, from zero for amounts and fitted for temperatures', () => {
    const watts = historyOption(input({ unit: 'W', zeroBased: true })).yAxis as Record<string, unknown>
    expect(watts).toMatchObject({ name: 'W', min: 0, scale: false })
    const celsius = historyOption(input({ unit: '°C' })).yAxis as Record<string, unknown>
    expect(celsius).toMatchObject({ name: '°C', min: undefined, scale: true })
  })

  it('shades the part of the window before the recording started, labelled when it is wide', () => {
    const wide = historyOption(
      input({
        series: { resolution: 'raw', points: [point(NOW - 2 * HOUR, 1)] },
        noData: { until: NOW - 2 * HOUR, label: 'Recording since 10:00' },
      }),
    )
    const area = (wide.series as SeriesList)[0]!.markArea!
    expect(area.data).toEqual([[{ xAxis: DAY_WINDOW.min }, { xAxis: NOW - 2 * HOUR }]])
    expect(area.label).toMatchObject({ show: true, formatter: 'Recording since 10:00' })

    const narrow = historyOption(
      input({
        series: { resolution: 'raw', points: [point(NOW - 20 * HOUR, 1)] },
        noData: { until: NOW - 20 * HOUR, label: 'Recording since 16:00' },
      }),
    )
    expect((narrow.series as SeriesList)[0]!.markArea!.label.show).toBe(false)
    expect((historyOption(input({})).series as SeriesList)[0]!.markArea).toBeUndefined()
  })

  it('draws enum states on a category axis', () => {
    const option = historyOption(
      input({
        kind: 'enum',
        series: {
          resolution: 'raw',
          points: [point(NOW - 2 * HOUR, null, { s: 'cool' }), point(NOW - HOUR, null, { s: 'heat' })],
        },
        enumValues: ['off', 'cool'],
      }),
    )
    expect((option.yAxis as { data: string[] }).data).toEqual(['off', 'cool', 'heat'])
    const [line] = option.series as SeriesList
    expect(line!.data).toEqual([
      [NOW - 2 * HOUR, 'cool'],
      [NOW - HOUR, 'heat'],
      [NOW, 'heat'],
    ])
  })

  it('labels the time axis inside the window only, four to twelve times', () => {
    for (const kind of ['number', 'enum'] as const) {
      const option = historyOption(input({ kind, series: { resolution: 'raw', points: [point(NOW, 1)] } }))
      const xAxis = option.xAxis as {
        min: number
        max: number
        minInterval: number
        maxInterval: number
        axisLabel: { showMinLabel?: boolean; showMaxLabel?: boolean }
      }
      expect(xAxis).toMatchObject({
        min: DAY_WINDOW.min,
        max: NOW,
        minInterval: 2 * HOUR,
        maxInterval: 6 * HOUR,
      })
      expect(xAxis.axisLabel).toMatchObject({ showMinLabel: false, showMaxLabel: false })
    }
  })

  it('formats the tooltip with escaped text', () => {
    const option = historyOption(input({ formatValue: () => '<b>1 W</b>' }))
    const tooltip = option.tooltip as { formatter: (params: unknown) => string }
    expect(tooltip.formatter([{ seriesName: 'Power', value: [NOW, 1] }])).toContain('&#60;b&#62;1 W')
    expect(tooltip.formatter([{ seriesName: 'other', value: [NOW, 1] }])).toBe('')
  })

  it('keeps the tooltip inside the chart, so it never runs past a phone screen', () => {
    expect(historyOption(input({})).tooltip).toMatchObject({ confine: true })
  })
})

describe('axis times', () => {
  it('says the hour alone on the hour in 12-hour languages, and hours with minutes otherwise', () => {
    const en = axisTimeFormatter('en-US')
    const pt = axisTimeFormatter('pt-BR')
    const eightPm = new Date(2026, 9, 21, 20, 0).getTime()
    expect(en(eightPm, false)).toBe('8 PM')
    expect(en(eightPm + 30 * MINUTE, false)).toBe('8:30 PM')
    expect(pt(eightPm - 12 * HOUR, false)).toBe('08:00')
    expect(pt(eightPm, false)).toBe('20:00')
    expect(pt(eightPm, true)).toMatch(/^21 de out/)
  })
})

describe('on/off timeline', () => {
  it('covers the window: nothing known before the first reading, each one held until the next', () => {
    expect(stateSegments([point(NOW - 6 * HOUR, 1), point(NOW - 2 * HOUR, 0)], DAY_WINDOW)).toEqual([
      { start: DAY_WINDOW.min, end: NOW - 6 * HOUR, on: null },
      { start: NOW - 6 * HOUR, end: NOW - 2 * HOUR, on: true },
      { start: NOW - 2 * HOUR, end: NOW, on: false },
    ])
  })

  it('merges repeated states and clips to the window', () => {
    expect(
      stateSegments([point(NOW - 30 * HOUR, 1), point(NOW - 3 * HOUR, 1), point(NOW - HOUR, 0)], DAY_WINDOW),
    ).toEqual([
      { start: DAY_WINDOW.min, end: NOW - HOUR, on: true },
      { start: NOW - HOUR, end: NOW, on: false },
    ])
    expect(stateSegments([], DAY_WINDOW)).toEqual([{ start: DAY_WINDOW.min, end: NOW, on: null }])
  })
})

describe('series helpers', () => {
  it('does not extend an unknown last value or a point at the end', () => {
    expect(extendToEnd([[1, null]], 5)).toEqual([[1, null]])
    expect(extendToEnd([[5, 3]], 5)).toEqual([[5, 3]])
    expect(extendToEnd([], 5)).toEqual([])
  })

  it('lists enum values in the given order, then the ones seen', () => {
    expect(enumCategories([{ s: 'b' }, { s: null }, { s: 'a' }, { s: 'b' }], ['a'])).toEqual(['a', 'b'])
  })

  it('escapes HTML', () => {
    expect(escapeHtml(`<a href="x">'&'</a>`)).toBe('&#60;a href=&#34;x&#34;&#62;&#39;&#38;&#39;&#60;/a&#62;')
  })
})

describe('recent raw tail', () => {
  it('starts the tail where the last statistics bucket ends, else the last 6 hours', () => {
    const stats = { resolution: '5m', points: [point(NOW - 20 * MINUTE, 40), point(NOW - 15 * MINUTE, 42)] }
    expect(rawTailFrom(stats, NOW)).toBe(new Date(NOW - 10 * MINUTE).toISOString())
    expect(rawTailFrom({ resolution: '1h', points: [point(NOW - 3 * HOUR, 40)] }, NOW)).toBe(
      new Date(NOW - 2 * HOUR).toISOString(),
    )
    expect(rawTailFrom({ resolution: '5m', points: [] }, NOW)).toBe(new Date(NOW - 6 * HOUR).toISOString())
    // Raw series need no tail.
    expect(rawTailFrom({ resolution: 'raw', points: [point(NOW - HOUR, 1)] }, NOW)).toBeNull()
  })

  it('appends the raw changes after the last bucket to the statistics', () => {
    const stats = {
      attributeId: 7,
      unit: 'W',
      resolution: '5m',
      points: [point(NOW - 20 * MINUTE, 40, { min: 38, max: 44 }), point(NOW - 15 * MINUTE, 42)],
    }
    const raw = {
      attributeId: 7,
      unit: 'W',
      resolution: 'raw',
      points: [point(NOW - 11 * MINUTE, 41), point(NOW - 10 * MINUTE, 43), point(NOW - 2 * MINUTE, 50)],
    }
    const merged = withRawTail(stats, raw)
    expect(merged.resolution).toBe('5m')
    expect(merged.points.map((entry) => entry.v)).toEqual([40, 42, 43, 50])
    // Nothing from the statistics: the raw changes alone.
    expect(withRawTail({ ...stats, points: [] }, raw).points).toHaveLength(3)
  })
})

describe('energy', () => {
  const rows = [
    { hourStartUtc: new Date(NOW - 3 * HOUR).toISOString(), wh: 300, estimated: false },
    { hourStartUtc: new Date(NOW - 3 * HOUR).toISOString(), wh: 50, estimated: true },
    { hourStartUtc: new Date(NOW - 2 * HOUR).toISOString(), wh: 900, estimated: false },
    { hourStartUtc: new Date(NOW - 26 * HOUR).toISOString(), wh: 120, estimated: false },
  ]

  it('sums the endpoints per hour', () => {
    const hours = bucketEnergy(rows, 'hour')
    expect(hours.map((entry) => entry.wh)).toEqual([120, 350, 900])
    expect(hours[1]!.estimated).toBe(true)
    expect(totalWh(rows)).toBe(1370)
  })

  it('sums per local day', () => {
    const days = bucketEnergy(rows, 'day')
    expect(days.reduce((sum, entry) => sum + entry.wh, 0)).toBe(1370)
    for (const day of days) {
      const date = new Date(day.start)
      expect(date.getHours() + date.getMinutes()).toBe(0)
    }
  })

  it('picks kWh once a bucket reaches 1 kWh', () => {
    expect(energyUnit([{ wh: 999 }])).toBe('Wh')
    expect(energyUnit([{ wh: 1500 }])).toBe('kWh')
  })

  it('gives small amounts the decimals their axis needs, so ticks never all read "0"', () => {
    expect(energyDigits([{ wh: 0.4 }, { wh: 0.1 }], 'Wh')).toBe(2)
    expect(energyDigits([{ wh: 4 }], 'Wh')).toBe(1)
    expect(energyDigits([{ wh: 40 }], 'Wh')).toBe(0)
    expect(energyDigits([], 'Wh')).toBe(0)
    expect(energyDigits([{ wh: 1500 }], 'kWh')).toBe(2)
  })

  it('has a slot for every bucket of the range, ending with the one now is in', () => {
    const hours = energySlots('24h', NOW + 20 * MINUTE)
    expect(hours).toHaveLength(24)
    expect(hours.at(-1)).toBe(NOW)
    expect(hours[0]).toBe(NOW - 23 * HOUR)
    const days = energySlots('7d', NOW)
    expect(days).toHaveLength(7)
    expect(new Date(days.at(-1)!).getHours()).toBe(0)
    expect(energySlots('30d', NOW)).toHaveLength(30)
  })

  it('labels at least four slots at phone width, more on wider charts', () => {
    expect(labelStep(24, 300)).toBe(6)
    expect(labelStep(24, 1000)).toBe(2)
    expect(labelStep(7, 300)).toBe(2)
    expect(labelStep(30, 300)).toBe(8)
  })

  it('draws a bar per slot, marks estimates and labels the current slot', () => {
    const slots = energySlots('24h', NOW)
    const option = energyOption({
      buckets: bucketEnergy(rows, 'hour'),
      slots,
      bucket: 'hour',
      palette: PALETTE,
      unit: 'kWh',
      step: 6,
      formatNumber: String,
      formatTime: (time) => String(time),
      estimatedLabel: 'estimated',
      name: 'Energy',
    })
    const [bars] = option.series as {
      data: { value: number; itemStyle: { color: string }; estimated: boolean }[]
    }[]
    expect(bars!.data).toHaveLength(24)
    const threeHoursAgo = slots.indexOf(NOW - 3 * HOUR)
    expect(bars!.data[threeHoursAgo]).toMatchObject({ value: 0.35, estimated: true })
    expect(bars!.data[threeHoursAgo]!.itemStyle.color).toBe(PALETTE.estimated)
    expect(bars!.data[slots.indexOf(NOW - 2 * HOUR)]!.itemStyle.color).toBe(PALETTE.line)
    expect(bars!.data.at(-1)!.value).toBe(0)

    const xAxis = option.xAxis as { axisLabel: { interval: (index: number) => boolean } }
    expect([23, 17, 11, 5].every((index) => xAxis.axisLabel.interval(index))).toBe(true)
    expect(xAxis.axisLabel.interval(22)).toBe(false)
    expect((option.yAxis as { name: string }).name).toBe('kWh')

    const tooltip = option.tooltip as { formatter: (params: unknown) => string; confine: boolean }
    expect(tooltip.formatter([{ data: { value: 0.35, estimated: true, start: NOW } }])).toContain(
      '(estimated)',
    )
    expect(tooltip.confine).toBe(true)
  })
})
