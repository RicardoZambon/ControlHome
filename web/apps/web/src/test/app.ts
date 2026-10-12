// Test helpers: the app's real plugins (i18n with throwing missing keys, Pinia, router on memory history).
import { mount, type ComponentMountingOptions } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Component } from 'vue'
import { createMemoryHistory } from 'vue-router'
import { createAppI18n, type AppLocale } from '../i18n'
import { createAppRouter } from '../router'

export function createTestPlugins(locale: AppLocale = 'en') {
  const i18n = createAppI18n({ locale })
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createAppRouter({ pinia, history: createMemoryHistory() })
  return { i18n, pinia, router }
}

/** Mounts a component with the app's plugins, after navigating to `path`. */
export async function mountWithApp<T extends Component>(
  component: T,
  options: { path?: string; locale?: AppLocale; mount?: ComponentMountingOptions<T> } = {},
) {
  const plugins = createTestPlugins(options.locale)
  await plugins.router.push(options.path ?? '/')
  await plugins.router.isReady()
  const wrapper = mount(component, {
    ...options.mount,
    attachTo: document.body,
    global: { plugins: [plugins.pinia, plugins.i18n, plugins.router], ...options.mount?.global },
  } as ComponentMountingOptions<T>)
  return { wrapper, ...plugins }
}

/** A JSON Response, as the API sends it. */
export function jsonResponse(status: number, body: unknown, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } })
}
