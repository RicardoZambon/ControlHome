import {
  Baby,
  Bath,
  BedDouble,
  BedSingle,
  Briefcase,
  Car,
  CookingPot,
  DoorOpen,
  Dumbbell,
  House,
  Laptop,
  Package,
  Shirt,
  ShowerHead,
  Sofa,
  Sun,
  Toilet,
  Trees,
  Tv,
  Utensils,
  WashingMachine,
} from '@lucide/vue'
import type { Component } from 'vue'

/**
 * Room icons: the key stored on the room (`Room.Icon`, lower-case kebab, a lucide name) → its component. Unknown keys
 * (e.g. `mdi:*` from a later importer) show the house.
 */
export const ROOM_ICONS: readonly { key: string; icon: Component }[] = [
  { key: 'house', icon: House },
  { key: 'sofa', icon: Sofa },
  { key: 'tv', icon: Tv },
  { key: 'cooking-pot', icon: CookingPot },
  { key: 'utensils', icon: Utensils },
  { key: 'bed-double', icon: BedDouble },
  { key: 'bed-single', icon: BedSingle },
  { key: 'baby', icon: Baby },
  { key: 'bath', icon: Bath },
  { key: 'shower-head', icon: ShowerHead },
  { key: 'toilet', icon: Toilet },
  { key: 'briefcase', icon: Briefcase },
  { key: 'laptop', icon: Laptop },
  { key: 'washing-machine', icon: WashingMachine },
  { key: 'shirt', icon: Shirt },
  { key: 'package', icon: Package },
  { key: 'dumbbell', icon: Dumbbell },
  { key: 'door-open', icon: DoorOpen },
  { key: 'car', icon: Car },
  { key: 'trees', icon: Trees },
  { key: 'sun', icon: Sun },
]

const byKey = new Map(ROOM_ICONS.map((entry) => [entry.key, entry.icon]))

export function roomIcon(key: string): Component {
  return byKey.get(key) ?? House
}
