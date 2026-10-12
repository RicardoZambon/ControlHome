import { beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import {
  declaredTokens,
  installTokensStylesheet,
  mockLightSystemTheme,
  normalizeTokens,
} from '../testing/theme'

const declared = declaredTokens()
const light = Object.fromEntries(declared.light)
const dark = Object.fromEntries(declared.dark)

/** useTheme and useThemeTokens keep module state: load both fresh (and from the same module graph) per test. */
async function load() {
  vi.resetModules()
  const { useTheme } = await import('./useTheme')
  const { useThemeTokens, themeTokenNames } = await import('./useThemeTokens')
  return { useTheme, useThemeTokens, themeTokenNames }
}

beforeEach(() => {
  localStorage.clear()
  document.documentElement.className = ''
  mockLightSystemTheme()
  installTokensStylesheet()
})

it('tests against the real tokens.css', () => {
  expect(declared.light.size).toBeGreaterThan(50)
  expect(dark.background).not.toBe(light.background)
})

describe('themeTokenNames', () => {
  it('lists every token declared in tokens.css once, without the leading dashes', async () => {
    const { themeTokenNames } = await load()

    expect([...themeTokenNames].sort()).toEqual(Object.keys(light).sort())
    expect(themeTokenNames).toContain('state-on-foreground')
    expect(themeTokenNames).toContain('room-8')
    expect(themeTokenNames).toContain('shadow-color')
  })
})

describe('useThemeTokens', () => {
  it('reads the light values of every token', async () => {
    const { useThemeTokens } = await load()

    expect(normalizeTokens(useThemeTokens().value)).toEqual(light)
  })

  it('re-reads the values when the theme changes', async () => {
    const { useTheme, useThemeTokens } = await load()
    const tokens = useThemeTokens()
    const { setMode } = useTheme()

    setMode('dark')
    await nextTick()
    expect(normalizeTokens(tokens.value)).toEqual(dark)

    setMode('light')
    await nextTick()
    expect(normalizeTokens(tokens.value)).toEqual(light)
  })

  it('starts from the persisted theme', async () => {
    localStorage.setItem('ch.theme', 'dark')
    const { useThemeTokens } = await load()

    expect(normalizeTokens(useThemeTokens().value)).toEqual(dark)
  })

  it('shares one ref between callers', async () => {
    const { useThemeTokens } = await load()

    expect(useThemeTokens()).toBe(useThemeTokens())
  })
})
