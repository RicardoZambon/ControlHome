import { BarChart, LineChart, type BarSeriesOption, type LineSeriesOption } from 'echarts/charts'
import {
  GridComponent,
  LegendComponent,
  TooltipComponent,
  type GridComponentOption,
  type LegendComponentOption,
  type TooltipComponentOption,
} from 'echarts/components'
import { use, type ComposeOption } from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'

/**
 * ECharts, tree-shaken: only the line and bar charts, the grid, tooltip and legend, drawn as SVG (crisp at any zoom,
 * and printable). Charts get every color from `useChartTheme()` / `useThemeTokens()` (docs/architecture.md §11).
 */
use([SVGRenderer, LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent])

export type ChartOption = ComposeOption<
  LineSeriesOption | BarSeriesOption | GridComponentOption | TooltipComponentOption | LegendComponentOption
>
