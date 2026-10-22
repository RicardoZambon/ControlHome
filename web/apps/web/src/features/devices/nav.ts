import { Lightbulb } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

// Everyone sees and (Members and Admins) controls the devices.
export const nav: NavItem[] = [
  { to: { name: 'devices' }, icon: Lightbulb, labelKey: 'devices.nav', order: 2, group: 'home' },
]
