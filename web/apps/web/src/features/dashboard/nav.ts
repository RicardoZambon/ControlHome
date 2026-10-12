import { House } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

export const nav: NavItem[] = [
  { to: { name: 'dashboard' }, icon: House, labelKey: 'dashboard.nav', order: 0, group: 'home' },
]
