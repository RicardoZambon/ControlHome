import { beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { toHexColor } from '../lib/color'
import { declaredTokens, installTokensStylesheet, mockLightSystemTheme } from '../testing/theme'

const declared = declaredTokens()
const hex = (theme: Map<string, string>, name: string) => toHexColor(theme.get(name)!)
const rooms = [1, 2, 3, 4, 5, 6, 7, 8].map((n) => `room-${n}`)

async function load() {
  vi.resetModules()
  const { useTheme } = await import('./useTheme')
  const { useChartTheme, buildChartTheme } = await import('./useChartTheme')
  return { useTheme, useChartTheme, buildChartTheme }
}

beforeEach(() => {
  localStorage.clear()
  document.documentElement.className = ''
  mockLightSystemTheme()
  installTokensStylesheet()
})

describe('buildChartTheme', () => {
  it('uses the room colors as the series palette', async () => {
    const { buildChartTheme } = await load()
    const theme = buildChartTheme(Object.fromEntries(declared.light))

    expect(theme.color).toEqual(rooms.map((name) => hex(declared.light, name)))
  })

  it('takes text, axis and grid colors from foreground, muted-foreground and border', async () => {
    const { buildChartTheme } = await load()
    const theme = buildChartTheme(Object.fromEntries(declared.light))
    const foreground = hex(declared.light, 'foreground')
    const muted = hex(declared.light, 'muted-foreground')
    const border = hex(declared.light, 'border')

    expect(theme.backgroundColor).toBe('transparent')
    expect(theme.textStyle.color).toBe(foreground)
    expect(theme.title.textStyle.color).toBe(foreground)
    expect(theme.title.subtextStyle.color).toBe(muted)
    expect(theme.legend.textStyle.color).toBe(muted)
    for (const axis of [theme.categoryAxis, theme.valueAxis, theme.logAxis, theme.timeAxis]) {
      expect(axis.axisLabel.color).toBe(muted)
      expect(axis.nameTextStyle.color).toBe(muted)
      expect(axis.axisLine.lineStyle.color).toBe(border)
      expect(axis.axisTick.lineStyle.color).toBe(border)
      expect(axis.splitLine.lineStyle.color).toEqual([border])
    }
  })

  it('styles tooltips like popovers and maps semantic series to state colors', async () => {
    const { buildChartTheme } = await load()
    const theme = buildChartTheme(Object.fromEntries(declared.light))

    expect(theme.tooltip.backgroundColor).toBe(hex(declared.light, 'popover'))
    expect(theme.tooltip.borderColor).toBe(hex(declared.light, 'border'))
    expect(theme.tooltip.textStyle.color).toBe(hex(declared.light, 'popover-foreground'))
    expect(theme.visualMap.color).toEqual(
      ['state-danger', 'state-energy', 'state-info'].map((name) => hex(declared.light, name)),
    )
    expect(theme.candlestick.itemStyle.color).toBe(hex(declared.light, 'state-on'))
    expect(theme.candlestick.itemStyle.color0).toBe(hex(declared.light, 'state-danger'))
  })

  it('emits only hex colors (ECharts cannot parse OKLCH)', async () => {
    const { buildChartTheme } = await load()
    const strings: string[] = []
    const collect = (value: unknown, key = ''): void => {
      if (typeof value === 'string' && /colou?r/i.test(key)) strings.push(value)
      else if (Array.isArray(value)) value.forEach((item) => collect(item, key))
      else if (value && typeof value === 'object')
        for (const [k, v] of Object.entries(value)) collect(v, /colou?r/i.test(k) ? k : key || k)
    }
    collect(buildChartTheme(Object.fromEntries(declared.dark)))

    expect(strings.length).toBeGreaterThan(20)
    expect(strings.filter((s) => s !== 'transparent' && !/^#[0-9a-f]{6}(?:[0-9a-f]{2})?$/.test(s))).toEqual(
      [],
    )
  })
})

describe('useChartTheme', () => {
  it('follows the active theme', async () => {
    const { useTheme, useChartTheme } = await load()
    const theme = useChartTheme()
    expect(theme.value.textStyle.color).toBe(hex(declared.light, 'foreground'))

    useTheme().setMode('dark')
    await nextTick()
    expect(theme.value.textStyle.color).toBe(hex(declared.dark, 'foreground'))
    expect(theme.value.color).toEqual(rooms.map((name) => hex(declared.dark, name)))
  })
})
