import { Layers } from '@lucide/vue'
import type { NavItem } from '../../lib/nav'

// Everyone sees the plans; only Admins get the editor (its route and the edit actions).
export const nav: NavItem[] = [
  { to: { name: 'floors' }, icon: Layers, labelKey: 'floors.nav', order: 5, group: 'home' },
]
