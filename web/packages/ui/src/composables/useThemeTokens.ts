import type { Ref } from 'vue'
import { effectScope, shallowRef, watch } from 'vue'
import tokensCss from '../styles/tokens.css?raw'
import { useTheme } from './useTheme'

/** Token values by name without the leading `--`: `{ background: 'oklch(...)', 'state-on': 'oklch(...)', radius: '0.75rem' }`. */
export type ThemeTokens = Record<string, string>

/** Every custom property declared in tokens.css (without `--`), in declaration order. tokens.css stays the only source. */
export const themeTokenNames: readonly string[] = [
  ...new Set(
    [...tokensCss.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(?:^|[\s{;])--([\w-]+)\s*:/g)].map(
      (match) => match[1]!,
    ),
  ),
]

/** Reads the current value of every token from the computed style of `element` (default `<html>`). */
export function readThemeTokens(element?: Element): ThemeTokens {
  if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') return {}
  const style = getComputedStyle(element ?? document.documentElement)
  const tokens: ThemeTokens = {}
  for (const name of themeTokenNames) {
    const value = style.getPropertyValue(`--${name}`).trim()
    if (value) tokens[name] = value
  }
  return tokens
}

let tokens: Ref<ThemeTokens> | undefined

function createTokens(): Ref<ThemeTokens> {
  // First, so the persisted theme's class is on <html> before the first read.
  const { resolved } = useTheme()
  const state = shallowRef<ThemeTokens>(readThemeTokens())
  if (typeof document === 'undefined') return state

  const refresh = () => (state.value = readThemeTokens())
  // 'post': runs after useTheme's synchronous class toggle, so the computed style already has the new theme.
  watch(resolved, refresh, { flush: 'post' })
  // First use before the stylesheet finished loading: read again once it has.
  if (document.readyState !== 'complete') window.addEventListener('load', refresh, { once: true })
  return state
}

/**
 * The resolved values of the design tokens for the active theme, re-read whenever the theme changes.
 * For code that cannot use Tailwind classes (charts, canvas, SVG computed in script). One shared ref;
 * treat it as read-only.
 */
export function useThemeTokens(): Ref<ThemeTokens> {
  tokens ??= effectScope(true).run(createTokens)!
  return tokens
}
