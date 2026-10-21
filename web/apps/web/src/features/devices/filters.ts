import type { RoomListItemDto } from '@controlhome/api-client'
import { endpointName, type EndpointEntry } from '../../stores/devices'

/** The device grid's filters (also the page's query string). `room` is a room id or `none`. */
export interface DeviceFilters {
  room?: string
  capability?: string
  integration?: string
  q?: string
}

export const NO_ROOM = 'none'

/** Lower case without accents, so "sala" finds "Sála" and "cozinha" finds "Cozinha". */
export function normalizeText(text: string): string {
  return text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
}

/** The entries that pass the filters (hidden endpoints only when `includeHidden`). */
export function filterEntries(
  entries: readonly EndpointEntry[],
  filters: DeviceFilters,
  options: { includeHidden: boolean; roomName: (roomId: number) => string | undefined },
): EndpointEntry[] {
  const words = normalizeText(filters.q ?? '')
    .split(/\s+/)
    .filter(Boolean)
  return entries.filter((entry) => {
    if (entry.endpoint.hidden && !options.includeHidden) return false
    if (filters.room) {
      if (filters.room === NO_ROOM ? entry.roomId !== null : String(entry.roomId) !== filters.room)
        return false
    }
    if (
      filters.capability &&
      !entry.endpoint.capabilities.some(
        (capability) => capability.capabilityId === filters.capability && !capability.retired,
      )
    )
      return false
    if (filters.integration && entry.device.integrationId !== filters.integration) return false
    if (words.length > 0) {
      const room = entry.roomId === null ? '' : (options.roomName(entry.roomId) ?? '')
      const haystack = normalizeText(
        `${endpointName(entry.device, entry.endpoint)} ${entry.device.name} ${room}`,
      )
      if (!words.every((word) => haystack.includes(word))) return false
    }
    return true
  })
}

export interface RoomGroup {
  /** `null`: not in a room. */
  roomId: number | null
  room?: RoomListItemDto
  entries: EndpointEntry[]
}

/** Entries grouped by room, in the rooms' order (floors, then rooms); devices without a room come last. */
export function groupByRoom(
  entries: readonly EndpointEntry[],
  rooms: readonly RoomListItemDto[],
): RoomGroup[] {
  const groups = new Map<number | null, EndpointEntry[]>()
  for (const entry of entries) {
    const key = entry.roomId !== null && rooms.some((room) => room.id === entry.roomId) ? entry.roomId : null
    const list = groups.get(key)
    if (list) list.push(entry)
    else groups.set(key, [entry])
  }
  const ordered: RoomGroup[] = []
  for (const room of rooms) {
    const list = groups.get(room.id)
    if (list) ordered.push({ roomId: room.id, room, entries: list })
  }
  const unassigned = groups.get(null)
  if (unassigned) ordered.push({ roomId: null, entries: unassigned })
  return ordered
}
