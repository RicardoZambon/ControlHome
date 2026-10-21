import { ArchiveRestore, DatabaseZap, House } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

// Admins look after the home and its data: occasional tasks, so phones reach them from the menu only.
export const nav: NavItem[] = [
  {
    to: { name: 'settings-home' },
    icon: House,
    labelKey: 'settings.home.nav',
    roles: ['Admin'],
    order: 6,
    group: 'settings',
    tabBar: false,
  },
  {
    to: { name: 'settings-history' },
    icon: DatabaseZap,
    labelKey: 'settings.history.nav',
    roles: ['Admin'],
    order: 7,
    group: 'settings',
    tabBar: false,
  },
  {
    to: { name: 'settings-backup' },
    icon: ArchiveRestore,
    labelKey: 'settings.backup.nav',
    roles: ['Admin'],
    order: 8,
    group: 'settings',
    tabBar: false,
  },
]
