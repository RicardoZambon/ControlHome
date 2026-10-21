import type { DeviceSummaryDto, RoomListItemDto } from '@controlhome/api-client'
import { normalizeText } from '../../components/floorplan/placements'

/**
 * The import review after a new integration: every device it brought, kept (shown) or hidden, and the room each one
 * goes to — the room its vendor app suggested, else one suggested from its name ("Living room light" → Living room).
 */

function words(text: string): string {
  return ` ${normalizeText(text)
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim()} `
}

/**
 * Rooms as people name them in English and in Portuguese (lower case, without accents): a device a vendor app named
 * "Bedroom air conditioner" goes to a home's "Quarto", "Luz da sala" to its "Living room".
 */
const ROOM_SYNONYMS: readonly (readonly string[])[] = [
  ['living room', 'living', 'lounge', 'sala', 'sala de estar'],
  ['dining room', 'sala de jantar'],
  ['kitchen', 'cozinha'],
  ['bedroom', 'quarto', 'dormitorio'],
  ['master bedroom', 'suite', 'quarto principal'],
  ['bathroom', 'toilet', 'wc', 'banheiro', 'lavabo'],
  ['laundry', 'laundry room', 'lavanderia', 'area de servico'],
  ['office', 'study', 'escritorio'],
  ['garage', 'garagem'],
  ['entrance', 'entryway', 'hall', 'hallway', 'entrada', 'hall de entrada', 'corredor'],
  ['backyard', 'garden', 'yard', 'quintal', 'jardim'],
  ['porch', 'balcony', 'varanda', 'sacada'],
]

/**
 * The room a device most likely is in: a room whose whole name appears in the device's name (by words, without case
 * or accents); the longest such name wins ("Master bedroom" over "Bedroom"), the first room on a tie. Failing that, a
 * room known by the same name in the other language ("Quarto" for "Bedroom air conditioner"). `null` if none.
 */
export function suggestRoom(
  deviceName: string,
  rooms: readonly Pick<RoomListItemDto, 'id' | 'name'>[],
): number | null {
  const name = words(deviceName)
  let best: { id: number; length: number } | null = null
  for (const room of rooms) {
    const roomWords = words(room.name).trim()
    if (!roomWords || !name.includes(` ${roomWords} `)) continue
    if (!best || roomWords.length > best.length) best = { id: room.id, length: roomWords.length }
  }
  if (best) return best.id

  for (const room of rooms) {
    const roomWords = words(room.name).trim()
    for (const group of ROOM_SYNONYMS) {
      if (!group.includes(roomWords)) continue
      for (const term of group) {
        if (term === roomWords || !name.includes(` ${term} `)) continue
        if (!best || term.length > best.length) best = { id: room.id, length: term.length }
      }
    }
  }
  return best?.id ?? null
}

/** Where a room suggestion comes from: the integration (its vendor app's room) or the device's name. */
export interface RoomSuggestion {
  roomId: number
  source: 'integration' | 'name'
}

/**
 * The room suggested for a device of a new integration: the one the integration already put it in (a device just
 * imported has a room only when its vendor app named one of this home's rooms), else the room its name points to.
 */
export function roomSuggestion(
  device: Pick<DeviceSummaryDto, 'name' | 'roomId'>,
  rooms: readonly Pick<RoomListItemDto, 'id' | 'name'>[],
): RoomSuggestion | null {
  if (device.roomId !== null) return { roomId: device.roomId, source: 'integration' }
  const byName = suggestRoom(device.name, rooms)
  return byName === null ? null : { roomId: byName, source: 'name' }
}

/** One device in the review: keep it (shown) or not, and its room. */
export interface ReviewChoice {
  keep: boolean
  roomId: number | null
}

/** The starting choice for a device: kept, in the suggested room (if any). */
export function initialChoice(
  device: Pick<DeviceSummaryDto, 'name' | 'roomId'>,
  rooms: readonly Pick<RoomListItemDto, 'id' | 'name'>[],
): ReviewChoice {
  return { keep: true, roomId: roomSuggestion(device, rooms)?.roomId ?? null }
}

export type ReviewChange =
  | { kind: 'room'; device: DeviceSummaryDto; roomId: number | null }
  | { kind: 'hide'; device: DeviceSummaryDto }

/**
 * What "Finish" changes: a kept device whose room differs gets it (`PUT /api/devices/{id}`); a device not kept has its
 * endpoints hidden (it stays in the integration, out of dashboards and lists). Unchanged devices need nothing.
 */
export function reviewChanges(
  devices: readonly DeviceSummaryDto[],
  choices: ReadonlyMap<string, ReviewChoice>,
): ReviewChange[] {
  const changes: ReviewChange[] = []
  for (const device of devices) {
    const choice = choices.get(device.id)
    if (!choice) continue
    if (!choice.keep) {
      if (device.endpoints.some((endpoint) => !endpoint.hidden)) changes.push({ kind: 'hide', device })
    } else if (choice.roomId !== device.roomId) {
      changes.push({ kind: 'room', device, roomId: choice.roomId })
    }
  }
  return changes
}
