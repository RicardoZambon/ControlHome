import { BarChart, LineChart, type BarSeriesOption, type LineSeriesOption } from 'echarts/charts'
import {
  GridComponent,
  TooltipComponent,
  type GridComponentOption,
  type TooltipComponentOption,
} from 'echarts/components'
import { use, type ComposeOption } from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'

/**
 * ECharts, tree-shaken: only the line and bar charts, the grid and the tooltip, drawn as SVG (crisp at any zoom, and
 * printable). Charts get every color from `useChartTheme()` / `useThemeTokens()` (docs/architecture.md §11). Imported by
 * the chart components only, which load with the device page's History tab.
 */
use([SVGRenderer, LineChart, BarChart, GridComponent, TooltipComponent])

export type ChartOption = ComposeOption<
  LineSeriesOption | BarSeriesOption | GridComponentOption | TooltipComponentOption
>
