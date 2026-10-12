import { useTheme, type ThemeMode } from '@controlhome/ui'
import { isAppLocale, setLocale } from '../i18n'

const THEME_MODES: readonly ThemeMode[] = ['light', 'dark', 'system']

export function isThemeMode(value: unknown): value is ThemeMode {
  return typeof value === 'string' && (THEME_MODES as readonly string[]).includes(value)
}

/** Applies a user's saved language and theme (`MeDto.locale` / `MeDto.theme`) to this browser. */
export function applyPreferences(preferences: { locale: string; theme: string }): void {
  if (isThemeMode(preferences.theme)) useTheme().setMode(preferences.theme)
  if (isAppLocale(preferences.locale)) setLocale(preferences.locale)
}
