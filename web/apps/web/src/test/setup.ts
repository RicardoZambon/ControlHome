// Vitest setup for apps/web (jsdom).
import { beforeEach } from 'vitest'

// jsdom has no matchMedia (useTheme and the sidebar's media query use it): a light, desktop-sized system.
if (typeof window.matchMedia !== 'function') {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }),
  })
}

beforeEach(() => {
  localStorage.clear()
  document.documentElement.className = ''
})
