import { afterEach, describe, expect, it, vi } from 'vitest'
// public/theme-init.js runs before the app (no flash of the wrong theme); keep it in step with useTheme/detectLocale.
import script from '../public/theme-init.js?raw'

const run = () => new Function(script)()
const html = document.documentElement

function systemPrefersDark(dark: boolean) {
  vi.spyOn(window, 'matchMedia').mockImplementation(
    (query: string) => ({ matches: dark && query.includes('dark'), media: query }) as MediaQueryList,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('theme-init.js', () => {
  it('applies a saved dark theme', () => {
    systemPrefersDark(false)
    localStorage.setItem('ch.theme', 'dark')
    run()
    expect(html.classList.contains('dark')).toBe(true)
  })

  it('applies a saved light theme over a dark system', () => {
    systemPrefersDark(true)
    localStorage.setItem('ch.theme', 'light')
    html.classList.add('dark')
    run()
    expect(html.classList.contains('dark')).toBe(false)
  })

  it('follows the system without a saved choice', () => {
    systemPrefersDark(true)
    run()
    expect(html.classList.contains('dark')).toBe(true)
  })

  it('sets the language: saved, else Portuguese for a pt browser, else English', () => {
    systemPrefersDark(false)
    localStorage.setItem('ch.locale', 'pt-BR')
    run()
    expect(html.lang).toBe('pt-BR')

    localStorage.clear()
    vi.stubGlobal('navigator', { language: 'pt-PT' })
    run()
    expect(html.lang).toBe('pt-BR')

    vi.stubGlobal('navigator', { language: 'de-DE' })
    run()
    expect(html.lang).toBe('en')
  })
})
