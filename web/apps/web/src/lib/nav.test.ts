import { House } from '@lucide/vue'
import { describe, expect, it } from 'vitest'
import { navItems, visibleNavGroups, type NavItem } from './nav'

const item = (name: string, order: number, extra: Partial<NavItem> = {}): NavItem => ({
  to: { name },
  icon: House,
  labelKey: `${name}.nav`,
  order,
  ...extra,
})

describe('visibleNavGroups', () => {
  const items = [
    item('users', 10, { roles: ['Admin'], group: 'settings' }),
    item('profile', 20, { group: 'settings' }),
    item('dashboard', 0),
  ]

  it('groups and orders the items', () => {
    const groups = visibleNavGroups(items, ['Admin'])
    expect(groups.map((group) => group.id)).toEqual(['home', 'settings'])
    expect(groups[1]!.items.map((entry) => entry.to.name)).toEqual(['users', 'profile'])
  })

  it('hides items the user has no role for, and empty groups', () => {
    const groups = visibleNavGroups(items, ['Member'])
    expect(groups.flatMap((group) => group.items.map((entry) => entry.to.name))).toEqual([
      'dashboard',
      'profile',
    ])
    expect(visibleNavGroups([items[0]!], ['Viewer'])).toEqual([])
  })
})

describe('navItems', () => {
  it('collects every feature nav.ts', () => {
    const names = navItems.map((entry) => entry.to.name)
    expect(names).toEqual(expect.arrayContaining(['dashboard', 'users', 'profile']))
    expect(navItems.find((entry) => entry.to.name === 'users')?.roles).toEqual(['Admin'])
  })
})
