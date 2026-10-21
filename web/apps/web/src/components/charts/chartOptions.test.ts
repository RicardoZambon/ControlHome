import type { HistoryPointDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import {
  bucketEnergy,
  energyBucket,
  energyOption,
  energyUnit,
  enumCategories,
  escapeHtml,
  extendToEnd,
  fitWindow,
  historyOption,
  isHistoryRange,
  rangeQuery,
  rawTailFrom,
  timeWindow,
  totalWh,
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
}

const NOW = Date.parse('2026-10-21T12:00:00Z')
const HOUR = 3_600_000

function point(t: number, v: number | null, extra: Partial<HistoryPointDto> = {}): HistoryPointDto {
  return { t: new Date(t).toISOString(), v, min: null, max: null, s: null, ...extra }
}

function input(extra: Partial<HistoryOptionInput>): HistoryOptionInput {
  return {
    series: { resolution: 'raw', points: [] },
    kind: 'number',
    window: { min: NOW - 24 * HOUR, max: NOW },
    palette: PALETTE,
    formatValue: (value) => `${value} W`,
    formatTick: (value) => String(value),
    formatTime: (time) => new Date(time).toISOString(),
    name: 'Power',
    ...extra,
  }
}

type SeriesList = { name?: string; data: unknown[]; step?: unknown; stack?: string; areaStyle?: unknown }[]

describe('ranges', () => {
  it('knows the ranges and their queries', () => {
    expect(isHistoryRange('7d')).toBe(true)
    expect(isHistoryRange('1y')).toBe(false)
    // The last 24 h is the server's default (its own clock).
    expect(rangeQuery('24h', NOW)).toEqual({})
    expect(rangeQuery('7d', NOW)).toEqual({ from: '2026-10-14T12:00:00.000Z' })
    expect(energyBucket('24h')).toBe('hour')
    expect(energyBucket('30d')).toBe('day')
  })

  it('stretches the window to the newest point', () => {
    expect(timeWindow('24h', NOW)).toEqual({ min: NOW - 24 * HOUR, max: NOW })
    expect(timeWindow('24h', NOW, NOW + 2 * HOUR)).toEqual({ min: NOW - 22 * HOUR, max: NOW + 2 * HOUR })
  })

  it('narrows the window to a history that starts within it, so a recent one is not a sliver', () => {
    const day = { min: NOW - 24 * HOUR, max: NOW }
    // History from before the window (or none): the whole window.
    expect(fitWindow(day, NOW - 30 * HOUR)).toEqual(day)
    expect(fitWindow(day, undefined)).toEqual(day)
    // Five hours of history: from a little before its first point.
    expect(fitWindow(day, NOW - 5 * HOUR)).toEqual({ min: NOW - 5.5 * HOUR, max: NOW })
    // Two minutes: at least ten minutes are shown.
    expect(fitWindow(day, NOW - 2 * 60_000)).toEqual({ min: NOW - 10 * 60_000, max: NOW })
    // Never wider than the window.
    expect(fitWindow(day, NOW - 23.9 * HOUR)).toEqual(day)
  })
})

describe('historyOption', () => {
  it('draws raw numbers as a step line held to the end of the window', () => {
    const option = historyOption(
      input({ series: { resolution: 'raw', points: [point(NOW - 3 * HOUR, 10), point(NOW - HOUR, 25)] } }),
    )
    const [line] = option.series as SeriesList
    expect(line!.step).toBe('end')
    expect(line!.data).toEqual([
      [NOW - 3 * HOUR, 10],
      [NOW - HOUR, 25],
      [NOW, 25],
    ])
  })

  it('draws statistics as the mean over the min–max band', () => {
    const option = historyOption(
      input({
        series: {
          resolution: '5m',
          points: [
            point(NOW - 10 * 60_000, 20, { min: 18, max: 23 }),
            point(NOW - 5 * 60_000, 21, { min: 20, max: 22 }),
          ],
        },
      }),
    )
    const series = option.series as SeriesList
    expect(series.map((entry) => entry.name)).toEqual(['min', 'range', 'Power'])
    expect(series[0]!.data).toEqual([
      [NOW - 600_000, 18],
      [NOW - 300_000, 20],
    ])
    expect(series[1]!.data).toEqual([
      [NOW - 600_000, 5],
      [NOW - 300_000, 2],
    ])
    expect(series[1]!.stack).toBe('band')
    // Statistics are not held to the end.
    expect(series[2]!.data).toHaveLength(2)
    expect(series[2]!.step).toBe(false)
  })

  it('holds statistics followed by recent raw changes to the end of the window', () => {
    const option = historyOption(
      input({
        series: {
          resolution: '5m',
          points: [point(NOW - 10 * 60_000, 20, { min: 18, max: 23 }), point(NOW - 3 * 60_000, 26)],
        },
        heldToEnd: true,
      }),
    )
    const line = (option.series as SeriesList).find((entry) => entry.name === 'Power')!
    expect(line.data.at(-1)).toEqual([NOW, 26])
  })

  it('marks the points of a series with only one or two of them, so a short one still shows', () => {
    const one = historyOption(
      input({ kind: 'bool', series: { resolution: 'raw', points: [point(NOW - 60_000, 1)] } }),
    )
    expect((one.series as { showSymbol?: boolean }[])[0]!.showSymbol).toBe(true)
    const many = historyOption(
      input({
        kind: 'bool',
        series: {
          resolution: 'raw',
          points: [point(NOW - 3 * HOUR, 1), point(NOW - 2 * HOUR, 0), point(NOW - HOUR, 1)],
        },
      }),
    )
    expect((many.series as { showSymbol?: boolean }[])[0]!.showSymbol).toBe(false)
  })

  it('draws booleans as on/off steps with their names on the axis', () => {
    const option = historyOption(
      input({
        kind: 'bool',
        series: { resolution: 'raw', points: [point(NOW - 2 * HOUR, 1), point(NOW - HOUR, 0)] },
        labelOf: (value) => (value === '1' ? 'On' : 'Off'),
      }),
    )
    const [line] = option.series as SeriesList
    expect(line!.data).toEqual([
      [NOW - 2 * HOUR, 1],
      [NOW - HOUR, 0],
      [NOW, 0],
    ])
    const yAxis = option.yAxis as { axisLabel: { formatter: (value: number) => string } }
    expect(yAxis.axisLabel.formatter(1)).toBe('On')
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

  it('formats the tooltip with escaped text', () => {
    const option = historyOption(input({ formatValue: () => '<b>1 W</b>' }))
    const tooltip = option.tooltip as { formatter: (params: unknown) => string }
    expect(tooltip.formatter([{ seriesName: 'Power', value: [NOW, 1] }])).toContain('&#60;b&#62;1 W')
    expect(tooltip.formatter([{ seriesName: 'other', value: [NOW, 1] }])).toBe('')
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
  const MINUTE = 60_000

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

  it('centres the bars in their bucket and marks estimates', () => {
    const option = energyOption({
      buckets: bucketEnergy(rows, 'hour'),
      bucket: 'hour',
      window: { min: NOW - 24 * HOUR, max: NOW },
      palette: PALETTE,
      unit: 'kWh',
      formatNumber: String,
      formatTime: (time) => String(time),
      estimatedLabel: 'estimated',
      name: 'Energy',
    })
    const [bars] = option.series as { data: { value: [number, number]; itemStyle: { color: string } }[] }[]
    expect(bars!.data[1]!.value).toEqual([NOW - 3 * HOUR + HOUR / 2, 0.35])
    expect(bars!.data[1]!.itemStyle.color).toBe(PALETTE.estimated)
    expect(bars!.data[2]!.itemStyle.color).toBe(PALETTE.line)
    const tooltip = option.tooltip as { formatter: (params: unknown) => string }
    expect(tooltip.formatter([{ value: [NOW, 0.35], data: { estimated: true } }])).toContain('(estimated)')
  })
})
