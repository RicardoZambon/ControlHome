import { BarChart, LineChart, type BarSeriesOption, type LineSeriesOption } from 'echarts/charts'
import {
  GridComponent,
  MarkAreaComponent,
  TooltipComponent,
  type GridComponentOption,
  type MarkAreaComponentOption,
  type TooltipComponentOption,
} from 'echarts/components'
import { use, type ComposeOption } from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'

/**
 * ECharts, tree-shaken: only the line and bar charts, the grid, the tooltip and mark areas (the shaded stretch before a
 * recording starts), drawn as SVG (crisp at any zoom, and printable). Charts get every color from `useChartTheme()` /
 * `useThemeTokens()` (docs/architecture.md §11). Imported by the chart components only, which load with the device
 * page's History tab.
 */
use([SVGRenderer, LineChart, BarChart, GridComponent, TooltipComponent, MarkAreaComponent])

export type ChartOption = ComposeOption<
  LineSeriesOption | BarSeriesOption | GridComponentOption | TooltipComponentOption | MarkAreaComponentOption
>

/** One point of a line: `[time, value]`, or an item with its own dot. */
export type LineDataItem = NonNullable<LineSeriesOption['data']>[number]
