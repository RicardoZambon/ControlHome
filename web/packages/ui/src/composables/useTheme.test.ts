import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type ChangeListener = (event: { matches: boolean }) => void

/** Installs a controllable `window.matchMedia('(prefers-color-scheme: dark)')`. */
function mockSystemTheme(dark: boolean) {
  const listeners = new Set<ChangeListener>()
  const query = {
    matches: dark,
    media: '(prefers-color-scheme: dark)',
    addEventListener: (_type: string, listener: ChangeListener) => listeners.add(listener),
    removeEventListener: (_type: string, listener: ChangeListener) => listeners.delete(listener),
  }
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: vi.fn(() => query),
  })
  return {
    setDark(matches: boolean) {
      query.matches = matches
      for (const listener of listeners) listener({ matches })
    },
  }
}

/** The theme state is a module singleton: load a fresh module for every test. */
async function loadUseTheme() {
  vi.resetModules()
  return (await import('./useTheme')).useTheme
}

const html = () => document.documentElement

beforeEach(() => {
  localStorage.clear()
  html().className = ''
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('useTheme', () => {
  it('follows the system preference when nothing is stored', async () => {
    mockSystemTheme(true)
    const { mode, resolved } = (await loadUseTheme())()

    expect(mode.value).toBe('system')
    expect(resolved.value).toBe('dark')
    expect(html().classList.contains('dark')).toBe(true)
  })

  it('reacts to system preference changes in system mode', async () => {
    const system = mockSystemTheme(false)
    const { resolved } = (await loadUseTheme())()
    expect(html().classList.contains('dark')).toBe(false)

    system.setDark(true)
    expect(resolved.value).toBe('dark')
    expect(html().classList.contains('dark')).toBe(true)

    system.setDark(false)
    expect(resolved.value).toBe('light')
    expect(html().classList.contains('dark')).toBe(false)
  })

  it('restores the persisted mode', async () => {
    mockSystemTheme(false)
    localStorage.setItem('ch.theme', 'dark')
    const { mode, resolved } = (await loadUseTheme())()

    expect(mode.value).toBe('dark')
    expect(resolved.value).toBe('dark')
    expect(html().classList.contains('dark')).toBe(true)
  })

  it('ignores an unknown persisted value', async () => {
    mockSystemTheme(false)
    localStorage.setItem('ch.theme', 'purple')
    const { mode } = (await loadUseTheme())()

    expect(mode.value).toBe('system')
  })

  it('setMode applies and persists the mode', async () => {
    mockSystemTheme(true)
    const { mode, resolved, setMode } = (await loadUseTheme())()

    setMode('light')
    expect(mode.value).toBe('light')
    expect(resolved.value).toBe('light')
    expect(html().classList.contains('dark')).toBe(false)
    expect(localStorage.getItem('ch.theme')).toBe('light')

    setMode('dark')
    expect(html().classList.contains('dark')).toBe(true)
    expect(localStorage.getItem('ch.theme')).toBe('dark')

    setMode('system')
    expect(resolved.value).toBe('dark')
    expect(localStorage.getItem('ch.theme')).toBe('system')
  })

  it('persists a mode written to the ref directly', async () => {
    mockSystemTheme(false)
    const { mode, resolved } = (await loadUseTheme())()

    mode.value = 'dark'
    expect(resolved.value).toBe('dark')
    expect(localStorage.getItem('ch.theme')).toBe('dark')
  })

  it('ignores system changes while an explicit mode is set', async () => {
    const system = mockSystemTheme(false)
    const { resolved, setMode } = (await loadUseTheme())()

    setMode('light')
    system.setDark(true)
    expect(resolved.value).toBe('light')
    expect(html().classList.contains('dark')).toBe(false)
  })

  it('ignores invalid modes passed at runtime', async () => {
    mockSystemTheme(false)
    const { mode, setMode } = (await loadUseTheme())()

    setMode('sepia' as never)
    expect(mode.value).toBe('system')
  })

  it('shares one state between callers', async () => {
    mockSystemTheme(false)
    const useTheme = await loadUseTheme()
    const first = useTheme()
    const second = useTheme()

    expect(second.mode).toBe(first.mode)
    first.setMode('dark')
    expect(second.resolved.value).toBe('dark')
  })

  it('follows a mode changed in another tab', async () => {
    mockSystemTheme(false)
    const { mode } = (await loadUseTheme())()

    window.dispatchEvent(new StorageEvent('storage', { key: 'ch.theme', newValue: 'dark' }))
    expect(mode.value).toBe('dark')
    expect(html().classList.contains('dark')).toBe(true)
  })

  it('works when storage is unavailable', async () => {
    mockSystemTheme(false)
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })
    const { mode, resolved, setMode } = (await loadUseTheme())()

    expect(mode.value).toBe('system')
    expect(() => setMode('dark')).not.toThrow()
    expect(resolved.value).toBe('dark')
  })

  it('treats a missing matchMedia as a light system theme', async () => {
    Object.defineProperty(window, 'matchMedia', { configurable: true, writable: true, value: undefined })
    const { resolved } = (await loadUseTheme())()

    expect(resolved.value).toBe('light')
  })
})
