import type { ComputedRef, Ref } from 'vue'
import { computed, effectScope, ref, watch } from 'vue'

/** The user's choice: a fixed theme, or follow the operating system (`prefers-color-scheme`). */
export type ThemeMode = 'light' | 'dark' | 'system'
/** The theme actually shown. */
export type ResolvedTheme = 'light' | 'dark'

export interface UseTheme {
  /** The selected mode. Writable: assigning it is the same as calling `setMode`. */
  mode: Ref<ThemeMode>
  /** The theme in effect after resolving `system`. */
  resolved: ComputedRef<ResolvedTheme>
  setMode(mode: ThemeMode): void
}

/** localStorage key of the selected mode. */
export const THEME_STORAGE_KEY = 'ch.theme'
const DARK_QUERY = '(prefers-color-scheme: dark)'
const MODES: readonly ThemeMode[] = ['light', 'dark', 'system']

function isThemeMode(value: unknown): value is ThemeMode {
  return typeof value === 'string' && (MODES as readonly string[]).includes(value)
}

// Storage can be missing (SSR) or throw (blocked cookies, sandboxed frames, full quota): fall back to `system`.
function readStoredMode(): ThemeMode {
  try {
    const stored = globalThis.localStorage?.getItem(THEME_STORAGE_KEY)
    return isThemeMode(stored) ? stored : 'system'
  } catch {
    return 'system'
  }
}

function writeStoredMode(mode: ThemeMode): void {
  try {
    globalThis.localStorage?.setItem(THEME_STORAGE_KEY, mode)
  } catch {
    // Not persisted; the mode still applies to this page.
  }
}

function systemDarkQuery(): MediaQueryList | undefined {
  try {
    return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia(DARK_QUERY)
      : undefined
  } catch {
    return undefined
  }
}

function createTheme(): UseTheme {
  const mode = ref<ThemeMode>(readStoredMode())
  const systemDark = ref(false)

  const query = systemDarkQuery()
  if (query) {
    systemDark.value = query.matches
    const onChange = (event: { matches: boolean }) => (systemDark.value = event.matches)
    if (typeof query.addEventListener === 'function') query.addEventListener('change', onChange)
    // Safari < 14 only has the deprecated MediaQueryList.addListener.
    else query.addListener?.(onChange)
  }

  if (typeof window !== 'undefined') {
    // Another tab changed the theme.
    window.addEventListener('storage', (event) => {
      if (event.key === THEME_STORAGE_KEY && isThemeMode(event.newValue)) mode.value = event.newValue
    })
  }

  const resolved = computed<ResolvedTheme>(() =>
    mode.value === 'system' ? (systemDark.value ? 'dark' : 'light') : mode.value,
  )

  // Synchronous, so the class (and the tokens it selects) is in place as soon as the mode changes.
  watch(mode, writeStoredMode, { flush: 'sync' })
  watch(
    resolved,
    (theme) => {
      if (typeof document !== 'undefined') document.documentElement.classList.toggle('dark', theme === 'dark')
    },
    { immediate: true, flush: 'sync' },
  )

  function setMode(next: ThemeMode): void {
    if (isThemeMode(next)) mode.value = next
  }

  return { mode, resolved, setMode }
}

let theme: UseTheme | undefined

/**
 * The app-wide theme: one shared state, created on first use. It toggles the `dark` class on `<html>`
 * (which selects the `.dark` tokens), persists the mode in `localStorage['ch.theme']` and follows
 * `prefers-color-scheme` while the mode is `system`. Safe to call without a DOM (SSR, tests): it then
 * resolves `system` to light and touches nothing.
 */
export function useTheme(): UseTheme {
  // A detached scope: the watchers live as long as the page, not as long as the first component that asked.
  theme ??= effectScope(true).run(createTheme)!
  return theme
}
