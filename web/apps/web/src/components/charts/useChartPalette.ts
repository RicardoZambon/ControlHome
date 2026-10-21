import { toHexColor, useThemeTokens } from '@controlhome/ui'
import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'
import type { ChartPalette } from './chartOptions'

/**
 * A chart's series colors from one design token (`state-energy`, `primary`, `room-3`, …), for the active theme: the line,
 * its fading fill, the statistics band and the lighter "estimated" shade. Charts never hard-code colors.
 */
export function useChartPalette(token: MaybeRefOrGetter<string>): ComputedRef<ChartPalette> {
  const tokens = useThemeTokens()
  return computed(() => {
    const value = tokens.value[toValue(token)] ?? tokens.value.primary
    const hex = (alpha?: number) => (value ? toHexColor(value, alpha) : 'transparent')
    return { line: hex(), area: hex(0.24), areaFade: hex(0), band: hex(0.16), estimated: hex(0.42) }
  })
}
