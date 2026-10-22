import { Blocks } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

// Admins connect the home to its devices; an occasional task, so phones reach it from the menu only.
export const nav: NavItem[] = [
  {
    to: { name: 'integrations' },
    icon: Blocks,
    labelKey: 'integrations.nav',
    roles: ['Admin'],
    order: 5,
    group: 'settings',
    tabBar: false,
  },
]
