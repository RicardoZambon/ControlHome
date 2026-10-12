// Test helpers for the theme composables (imported by *.test.ts only).
import { vi } from 'vitest'
import tokensCss from '../styles/tokens.css?raw'

/**
 * Whitespace-insensitive form of a token value: browsers and jsdom may serialize `oklch(0.2 0 0 / 0.5)` as
 * `oklch(0.2 0 0/0.5)`.
 */
export function normalizeTokenValue(value: string): string {
  return value
    .trim()
    .replace(/\s+/g, ' ')
    .replace(/\s*\/\s*/g, ' / ')
}

/** Normalizes every value of a token record (see `normalizeTokenValue`). */
export function normalizeTokens(tokens: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(tokens).map(([name, value]) => [name, normalizeTokenValue(value)]))
}

/** Token values declared in tokens.css, keyed without the leading `--`: light (`:root`) and dark (`.dark`). */
export function declaredTokens(): { light: Map<string, string>; dark: Map<string, string> } {
  const block = (selector: string) => {
    const start = tokensCss.indexOf(`${selector} {`)
    const body = tokensCss.slice(start, tokensCss.indexOf('}', start))
    return new Map(
      [...body.matchAll(/--([\w-]+)\s*:\s*([^;]+);/g)].map((m) => [m[1]!, normalizeTokenValue(m[2]!)]),
    )
  }
  const light = block(':root')
  return { light, dark: new Map([...light, ...block('.dark')]) }
}

/** Puts the real tokens.css into the document, so getComputedStyle resolves the custom properties. */
export function installTokensStylesheet(): void {
  document.getElementById('test-tokens')?.remove()
  const style = document.createElement('style')
  style.id = 'test-tokens'
  style.textContent = tokensCss
  document.head.append(style)
}

/** A light system theme (`prefers-color-scheme: light`) without change events. */
export function mockLightSystemTheme(): void {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: vi.fn(() => ({
      matches: false,
      media: '(prefers-color-scheme: dark)',
      addEventListener: () => {},
      removeEventListener: () => {},
    })),
  })
}
