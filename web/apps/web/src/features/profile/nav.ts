import { UserRound } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

export const nav: NavItem[] = [
  { to: { name: 'profile' }, icon: UserRound, labelKey: 'profile.nav', order: 20, group: 'settings' },
]
