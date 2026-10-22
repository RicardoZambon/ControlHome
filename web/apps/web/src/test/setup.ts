// Vitest setup for apps/web (jsdom).
import { beforeEach } from 'vitest'
import type { HubConnectionLike } from '../stores/realtime'

/** A hub connection that never connects: tests never open SignalR (the realtime store's tests use their own fake). */
function inertConnection(): HubConnectionLike {
  return {
    on: () => {},
    onreconnecting: () => {},
    onreconnected: () => {},
    onclose: () => {},
    start: () => new Promise<void>(() => {}),
    stop: () => Promise.resolve(),
  }
}

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

// jsdom has no ResizeObserver either (reka-ui's slider measures its thumb with it): one that never reports.
if (typeof globalThis.ResizeObserver !== 'function') {
  globalThis.ResizeObserver = class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  }
}

beforeEach(async () => {
  // Imported here, not at the top: a module this file imports would skip the test files' vi.mock(...) of its imports.
  const { setHubConnectionFactory } = await import('../stores/realtime')
  setHubConnectionFactory(inertConnection)
  localStorage.clear()
  document.documentElement.className = ''
})
