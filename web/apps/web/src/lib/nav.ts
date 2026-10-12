import type { Component } from 'vue'
import type { RouteLocationRaw } from 'vue-router'

export type NavGroup = 'home' | 'settings'

/** A sidebar entry, exported by a feature's `nav.ts` as `nav: NavItem[]`. */
export interface NavItem {
  /** Route location (prefer `{ name }`). */
  to: RouteLocationRaw & { name: string }
  icon: Component
  /** i18n key of the label. */
  labelKey: string
  /** Only users with one of these roles see the item (default: everyone signed in). */
  roles?: readonly string[]
  /** Position inside the group, ascending. */
  order: number
  /** Sidebar section (default `home`). */
  group?: NavGroup
}

export const NAV_GROUPS: readonly { id: NavGroup; labelKey: string }[] = [
  { id: 'home', labelKey: 'shell.nav.groups.home' },
  { id: 'settings', labelKey: 'shell.nav.groups.settings' },
]

const navModules = import.meta.glob<readonly NavItem[]>('../features/*/nav.ts', {
  eager: true,
  import: 'nav',
})

/** Every feature's nav items, ordered. */
export const navItems: readonly NavItem[] = Object.values(navModules)
  .flat()
  .sort((a, b) => a.order - b.order)

/** The items a user with `roles` may see, grouped in sidebar order (empty groups are left out). */
export function visibleNavGroups(
  items: readonly NavItem[],
  roles: readonly string[],
): { id: NavGroup; labelKey: string; items: NavItem[] }[] {
  const allowed = items
    .filter((item) => !item.roles?.length || item.roles.some((role) => roles.includes(role)))
    .sort((a, b) => a.order - b.order)
  return NAV_GROUPS.map((group) => ({
    ...group,
    items: allowed.filter((item) => (item.group ?? 'home') === group.id),
  })).filter((group) => group.items.length > 0)
}
