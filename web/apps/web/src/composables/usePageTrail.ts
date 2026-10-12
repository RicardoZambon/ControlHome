import { computed, onScopeDispose, shallowRef, watchEffect } from 'vue'
import type { RouteLocationRaw } from 'vue-router'

/** One step of the top bar's breadcrumb, after the home's name. The last one is the current page (no link). */
export interface TrailItem {
  label: string
  to?: RouteLocationRaw
}

const current = shallowRef<{ owner: symbol; items: readonly TrailItem[] } | null>(null)

/** The trail the current page set, or null (the top bar then shows the route's title). */
export const pageTrail = computed(() => current.value?.items ?? null)

/**
 * Lets a page put where it is in the top bar ("Floors › Ground floor › Edit plan") when the route's title is not
 * enough (it names the floor). `items` is reactive: return null until the page knows its names. Cleared when the page
 * goes away (unless the next page already set its own).
 */
export function usePageTrail(items: () => readonly TrailItem[] | null): void {
  const owner = Symbol('page-trail')
  watchEffect(() => {
    const next = items()
    if (next) current.value = { owner, items: next }
    else if (current.value?.owner === owner) current.value = null
  })
  onScopeDispose(() => {
    if (current.value?.owner === owner) current.value = null
  })
}
