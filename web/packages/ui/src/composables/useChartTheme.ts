import type { ComputedRef } from 'vue'
import { computed } from 'vue'
import { toHexColor } from '../lib/color'
import type { ThemeTokens } from './useThemeTokens'
import { useThemeTokens } from './useThemeTokens'

interface LineStyle {
  color: string
}

interface AxisTheme {
  axisLine: { show: boolean; lineStyle: LineStyle }
  axisTick: { show: boolean; lineStyle: LineStyle }
  axisLabel: { show: boolean; color: string }
  splitLine: { show: boolean; lineStyle: { color: string[] } }
  splitArea: { show: boolean; areaStyle: { color: string[] } }
  nameTextStyle: { color: string }
}

/**
 * An ECharts theme object (the shape `echarts.registerTheme(name, theme)` and vue-echarts' `theme` prop take).
 * Only colors come from here; layout and typography stay ECharts defaults. No ECharts dependency.
 */
export interface ChartTheme {
  /** Categorical series palette: the eight room colors. */
  color: string[]
  backgroundColor: string
  textStyle: { color: string }
  title: { textStyle: { color: string }; subtextStyle: { color: string } }
  legend: { textStyle: { color: string }; inactiveColor: string; pageTextStyle: { color: string } }
  tooltip: {
    backgroundColor: string
    borderColor: string
    textStyle: { color: string }
    axisPointer: { lineStyle: LineStyle; crossStyle: LineStyle }
  }
  axisPointer: {
    lineStyle: LineStyle
    crossStyle: LineStyle
    label: { backgroundColor: string; color: string }
  }
  categoryAxis: AxisTheme
  valueAxis: AxisTheme
  logAxis: AxisTheme
  timeAxis: AxisTheme
  dataZoom: {
    textStyle: { color: string }
    borderColor: string
    backgroundColor: string
    fillerColor: string
    handleStyle: { color: string; borderColor: string }
    moveHandleStyle: { color: string }
    dataBackground: { lineStyle: LineStyle; areaStyle: { color: string } }
    selectedDataBackground: { lineStyle: LineStyle; areaStyle: { color: string } }
  }
  /** Continuous scales (heat maps, temperatures): high to low = danger, energy, info. */
  visualMap: { color: string[]; textStyle: { color: string } }
  /** Rising (`color`) and falling (`color0`) values. */
  candlestick: { itemStyle: { color: string; color0: string; borderColor: string; borderColor0: string } }
  markPoint: { label: { color: string } }
}

const ROOMS = ['room-1', 'room-2', 'room-3', 'room-4', 'room-5', 'room-6', 'room-7', 'room-8'] as const

/** Builds the chart theme from token values (see `useThemeTokens`). Colors are `#rrggbb[aa]`, which ECharts parses. */
export function buildChartTheme(tokens: ThemeTokens): ChartTheme {
  // A missing token (no stylesheet, SSR) renders nothing rather than ECharts' default palette.
  const color = (name: string, alpha?: number) => {
    const value = tokens[name]
    return value ? toHexColor(value, alpha) : 'transparent'
  }

  const foreground = color('foreground')
  const muted = color('muted-foreground')
  const border = color('border')
  const background = color('background')

  const axis = (): AxisTheme => ({
    axisLine: { show: true, lineStyle: { color: border } },
    axisTick: { show: true, lineStyle: { color: border } },
    axisLabel: { show: true, color: muted },
    splitLine: { show: true, lineStyle: { color: [border] } },
    splitArea: { show: false, areaStyle: { color: [color('muted', 0.5), 'transparent'] } },
    nameTextStyle: { color: muted },
  })

  return {
    color: ROOMS.map((name) => color(name)),
    backgroundColor: 'transparent',
    textStyle: { color: foreground },
    title: { textStyle: { color: foreground }, subtextStyle: { color: muted } },
    legend: { textStyle: { color: muted }, inactiveColor: border, pageTextStyle: { color: muted } },
    tooltip: {
      backgroundColor: color('popover'),
      borderColor: border,
      textStyle: { color: color('popover-foreground') },
      axisPointer: { lineStyle: { color: muted }, crossStyle: { color: muted } },
    },
    axisPointer: {
      lineStyle: { color: muted },
      crossStyle: { color: muted },
      label: { backgroundColor: foreground, color: background },
    },
    categoryAxis: axis(),
    valueAxis: axis(),
    logAxis: axis(),
    timeAxis: axis(),
    dataZoom: {
      textStyle: { color: muted },
      borderColor: border,
      backgroundColor: 'transparent',
      fillerColor: color('primary', 0.15),
      handleStyle: { color: color('card'), borderColor: color('input') },
      moveHandleStyle: { color: color('input') },
      dataBackground: { lineStyle: { color: muted }, areaStyle: { color: color('muted') } },
      selectedDataBackground: {
        lineStyle: { color: color('primary') },
        areaStyle: { color: color('accent') },
      },
    },
    visualMap: {
      color: [color('state-danger'), color('state-energy'), color('state-info')],
      textStyle: { color: muted },
    },
    candlestick: {
      itemStyle: {
        color: color('state-on'),
        color0: color('state-danger'),
        borderColor: color('state-on'),
        borderColor0: color('state-danger'),
      },
    },
    markPoint: { label: { color: background } },
  }
}

/**
 * The ECharts theme for the active color theme; recomputed when the theme changes. Pass it to vue-echarts
 * (`<VChart :theme="chartTheme" />`) or `echarts.init(el, chartTheme.value)`; charts never hard-code colors.
 */
export function useChartTheme(): ComputedRef<ChartTheme> {
  const tokens = useThemeTokens()
  return computed(() => buildChartTheme(tokens.value))
}
