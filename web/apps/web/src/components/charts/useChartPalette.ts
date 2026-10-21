import { toHexColor, useTheme, useThemeTokens } from '@controlhome/ui'
import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'
import type { ChartPalette } from './chartOptions'

/**
 * A chart's series colors from one design token (`state-energy`, `primary`, `room-3`, …), for the active theme: the line,
 * its fading fill, the statistics band, the lighter "estimated" shade, and the muted shade over a period without data.
 * Translucent fills are stronger on the dark theme, where a faint tint of a color disappears into the card. Charts never
 * hard-code colors.
 */
export function useChartPalette(token: MaybeRefOrGetter<string>): ComputedRef<ChartPalette> {
  const tokens = useThemeTokens()
  const { resolved } = useTheme()
  return computed(() => {
    const dark = resolved.value === 'dark'
    const value = tokens.value[toValue(token)] ?? tokens.value.primary
    const hex = (alpha?: number) => (value ? toHexColor(value, alpha) : 'transparent')
    const muted = tokens.value.muted
    const mutedText = tokens.value['muted-foreground']
    return {
      line: hex(),
      area: hex(dark ? 0.32 : 0.24),
      areaFade: hex(0),
      band: hex(dark ? 0.3 : 0.16),
      estimated: hex(0.42),
      shade: muted ? toHexColor(muted, dark ? 0.55 : 0.7) : 'transparent',
      shadeText: mutedText ? toHexColor(mutedText) : 'transparent',
    }
  })
}
