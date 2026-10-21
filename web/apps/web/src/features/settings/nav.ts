import { ArchiveRestore, History } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

// Admins look after the home's data: occasional tasks, so phones reach them from the menu only.
export const nav: NavItem[] = [
  {
    to: { name: 'settings-history' },
    icon: History,
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
