import type { DeviceSummaryDto, RoomListItemDto } from '@controlhome/api-client'
import { normalizeText } from '../../components/floorplan/placements'

/**
 * The import review after a new integration: every device it brought, kept (shown) or hidden, and the room each one
 * goes to — suggested from its name ("Living room light" → Living room) when the device has none yet.
 */

function words(text: string): string {
  return ` ${normalizeText(text)
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim()} `
}

/**
 * The room a device most likely is in: a room whose whole name appears in the device's name (by words, without case
 * or accents); the longest such name wins ("Master bedroom" over "Bedroom"), the first room on a tie. `null` if none.
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
  return best?.id ?? null
}

/** One device in the review: keep it (shown) or not, and its room. */
export interface ReviewChoice {
  keep: boolean
  roomId: number | null
}

/** The starting choice for a device: kept, in its own room, else the suggested one. */
export function initialChoice(
  device: Pick<DeviceSummaryDto, 'name' | 'roomId'>,
  rooms: readonly Pick<RoomListItemDto, 'id' | 'name'>[],
): ReviewChoice {
  return { keep: true, roomId: device.roomId ?? suggestRoom(device.name, rooms) }
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
