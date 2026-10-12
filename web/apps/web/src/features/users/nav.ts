import { UsersRound } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

export const nav: NavItem[] = [
  {
    to: { name: 'users' },
    icon: UsersRound,
    labelKey: 'users.nav',
    roles: ['Admin'],
    order: 10,
    group: 'settings',
  },
]
