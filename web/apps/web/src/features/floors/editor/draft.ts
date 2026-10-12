import type {
  FloorBackgroundDto,
  FloorDto,
  SaveFloorLayoutRequest,
  UpdateFloorRequest,
} from '@controlhome/api-client'
import { NAME_MAX_LENGTH, validateRoomPolygon, type Point } from '../../../components/floorplan/geometry'
import { ROOM_COLOR_TOKENS } from '../../../components/floorplan/roomColors'

/**
 * The editor's working copy of a floor. Immutable: every edit makes a new draft (see `useCommandStack`), sharing what
 * did not change. Rooms are identified by a client `key`; server ids live next to the draft (`ids`), so undoing past a
 * save never resurrects an id the server already deleted.
 */
export interface DraftRoom {
  key: string
  name: string
  icon: string
  colorToken: string
  polygon: Point[]
}

export interface DraftFloor {
  name: string
  level: number
  widthCm: number
  heightCm: number
  gridCm: number
}

export interface Draft {
  floor: DraftFloor
  rooms: DraftRoom[]
  background: FloorBackgroundDto | null
}

/** Mirrors `FloorRules`. */
export const FLOOR_LIMITS = {
  minLevel: -10,
  maxLevel: 100,
  minSizeCm: 100,
  maxSizeCm: 100_000,
  minGridCm: 1,
  maxGridCm: 1_000,
} as const

export const DEFAULT_ROOM_ICON = 'house'

/** The key of a room loaded from the server. */
export const serverRoomKey = (id: number) => `room-${id}`

let newRoomCounter = 0
/** A key for a room drawn in this session. */
export function newRoomKey(): string {
  newRoomCounter += 1
  return `new-${newRoomCounter}`
}

export function draftFromFloor(floor: FloorDto): { draft: Draft; ids: Map<string, number> } {
  const ids = new Map<string, number>()
  const rooms = floor.rooms.map((room) => {
    const key = serverRoomKey(room.id)
    ids.set(key, room.id)
    return {
      key,
      name: room.name,
      icon: room.icon,
      colorToken: room.colorToken,
      polygon: room.polygon.map((p) => ({ x: p.x, y: p.y })),
    }
  })
  return {
    draft: {
      floor: {
        name: floor.name,
        level: floor.level,
        widthCm: floor.widthCm,
        heightCm: floor.heightCm,
        gridCm: floor.gridCm,
      },
      rooms,
      background: floor.background ? { ...floor.background } : null,
    },
    ids,
  }
}

/** A tenth of a millimetre is plenty for a room outline (and keeps float noise out of the request). */
const round = (value: number) => Math.round(value * 100) / 100

export function layoutRequest(
  draft: Draft,
  ids: ReadonlyMap<string, number>,
  rowVersion: string,
): SaveFloorLayoutRequest {
  return {
    rowVersion,
    rooms: draft.rooms.map((room) => ({
      id: ids.get(room.key) ?? null,
      name: room.name.trim(),
      icon: room.icon,
      colorToken: room.colorToken,
      polygon: room.polygon.map((p) => ({ x: round(p.x), y: round(p.y) })),
    })),
  }
}

export function floorRequest(
  draft: Draft,
  rowVersion: string,
  size: { widthCm: number; heightCm: number } = draft.floor,
): UpdateFloorRequest {
  return {
    name: draft.floor.name.trim(),
    level: draft.floor.level,
    widthCm: size.widthCm,
    heightCm: size.heightCm,
    gridCm: draft.floor.gridCm,
    background: draft.background,
    rowVersion,
  }
}

const sameJson = (a: unknown, b: unknown) => a === b || JSON.stringify(a) === JSON.stringify(b)

export const sameRooms = (a: Draft, b: Draft) => sameJson(a.rooms, b.rooms)
export const sameFloor = (a: Draft, b: Draft) =>
  sameJson(a.floor, b.floor) && sameJson(a.background, b.background)

export type SaveStep = { kind: 'floor'; widthCm: number; heightCm: number } | { kind: 'layout' }

/**
 * The requests a save needs, in order. Floor properties (incl. background) and rooms are separate endpoints, each
 * checking the size: when both changed, the floor first grows to fit the old and the new rooms, then the rooms are
 * saved, then the floor shrinks to its final size if it has to.
 */
export function planSave(saved: Draft, draft: Draft): SaveStep[] {
  const floorChanged = !sameFloor(saved, draft)
  const roomsChanged = !sameRooms(saved, draft)
  const final = { widthCm: draft.floor.widthCm, heightCm: draft.floor.heightCm }
  if (!roomsChanged) return floorChanged ? [{ kind: 'floor', ...final }] : []
  if (!floorChanged) return [{ kind: 'layout' }]
  const interim = {
    widthCm: Math.max(saved.floor.widthCm, final.widthCm),
    heightCm: Math.max(saved.floor.heightCm, final.heightCm),
  }
  const steps: SaveStep[] = [{ kind: 'floor', ...interim }, { kind: 'layout' }]
  if (interim.widthCm !== final.widthCm || interim.heightCm !== final.heightCm) {
    steps.push({ kind: 'floor', ...final })
  }
  return steps
}

/** An error to show: an i18n key and its parameters. */
export interface EditorMessage {
  key: string
  params?: Record<string, unknown>
}

export interface RoomErrors {
  name?: EditorMessage
  polygon?: EditorMessage
  /** Anything else the server refused about the room (unknown id, icon, color). */
  other?: EditorMessage
}

export type FloorField = 'name' | 'level' | 'widthCm' | 'heightCm' | 'gridCm' | 'background' | 'rooms'

export interface DraftErrors {
  rooms: Record<string, RoomErrors>
  floor: Partial<Record<FloorField, EditorMessage>>
  count: number
}

function countErrors(errors: Omit<DraftErrors, 'count'>): number {
  let count = Object.keys(errors.floor).length
  for (const room of Object.values(errors.rooms)) count += Object.keys(room).length
  return count
}

function nameError(name: string): EditorMessage | undefined {
  const trimmed = name.trim()
  if (!trimmed) return { key: 'validation.required' }
  if (trimmed.length > NAME_MAX_LENGTH)
    return { key: 'validation.maxLength', params: { max: NAME_MAX_LENGTH } }
  return undefined
}

function rangeError(value: number, min: number, max: number): EditorMessage | undefined {
  if (!Number.isInteger(value)) return { key: 'validation.number' }
  if (value < min || value > max) return { key: 'floors.validation.range', params: { min, max } }
  return undefined
}

/** What the server would reject, checked in the browser (the same rules and codes). */
export function validateDraft(draft: Draft): DraftErrors {
  const floor: DraftErrors['floor'] = {}
  const set = (field: FloorField, message: EditorMessage | undefined) => {
    if (message) floor[field] = message
  }
  set('name', nameError(draft.floor.name))
  set('level', rangeError(draft.floor.level, FLOOR_LIMITS.minLevel, FLOOR_LIMITS.maxLevel))
  set('widthCm', rangeError(draft.floor.widthCm, FLOOR_LIMITS.minSizeCm, FLOOR_LIMITS.maxSizeCm))
  set('heightCm', rangeError(draft.floor.heightCm, FLOOR_LIMITS.minSizeCm, FLOOR_LIMITS.maxSizeCm))
  set('gridCm', rangeError(draft.floor.gridCm, FLOOR_LIMITS.minGridCm, FLOOR_LIMITS.maxGridCm))

  const rooms: DraftErrors['rooms'] = {}
  for (const room of draft.rooms) {
    const errors: RoomErrors = {}
    const name = nameError(room.name)
    if (name) errors.name = name
    const code = validateRoomPolygon(room.polygon, draft.floor.widthCm, draft.floor.heightCm)
    if (code) errors.polygon = { key: `errors.${code}` }
    if (Object.keys(errors).length > 0) rooms[room.key] = errors
  }
  return { floor, rooms, count: countErrors({ floor, rooms }) }
}

const ROOM_PATH = /^rooms\[(\d+)\]\.(\w+)$/

/** The message for one server field error: its code when it is one (`geometry.*`, `files.*`), else "isn't valid". */
function messageFor(messages: readonly string[]): EditorMessage {
  const code = messages.find((message) => /^[a-z_]+\.[a-z_.]+$/.test(message))
  return code ? { key: `errors.${code}` } : { key: 'validation.invalid' }
}

/**
 * The field errors of a refused save (`validation.failed`), on the rooms they belong to: `rooms[i]` is the i-th room
 * that was sent (`sentKeys[i]`).
 */
export function errorsFromProblem(
  fieldErrors: Record<string, string[]>,
  sentKeys: readonly string[],
): DraftErrors {
  const rooms: DraftErrors['rooms'] = {}
  const floor: DraftErrors['floor'] = {}
  for (const [path, messages] of Object.entries(fieldErrors)) {
    const match = ROOM_PATH.exec(path)
    if (match) {
      const key = sentKeys[Number(match[1])]
      if (!key) continue
      const field = match[2] === 'name' ? 'name' : match[2] === 'polygon' ? 'polygon' : 'other'
      rooms[key] = {
        ...rooms[key],
        [field]: field === 'name' ? { key: 'validation.invalid' } : messageFor(messages),
      }
      continue
    }
    const root = path.split('.')[0] as FloorField
    if (['name', 'level', 'widthCm', 'heightCm', 'gridCm', 'background', 'rooms'].includes(root)) {
      floor[root] = messageFor(messages)
    }
  }
  return { rooms, floor, count: countErrors({ rooms, floor }) }
}

/** "Room 3": the first free number after the rooms there are. */
export function nextRoomName(rooms: readonly DraftRoom[], base: string): string {
  const taken = new Set(rooms.map((room) => room.name.trim().toLocaleLowerCase()))
  let n = rooms.length + 1
  while (taken.has(`${base} ${n}`.toLocaleLowerCase())) n += 1
  return `${base} ${n}`
}

/** The least used room color (earlier in the palette on ties), so neighbours tend to differ. */
export function nextColorToken(rooms: readonly DraftRoom[]): string {
  const uses = new Map<string, number>(ROOM_COLOR_TOKENS.map((token) => [token, 0]))
  for (const room of rooms)
    if (uses.has(room.colorToken)) uses.set(room.colorToken, uses.get(room.colorToken)! + 1)
  let best: string = ROOM_COLOR_TOKENS[0]
  for (const token of ROOM_COLOR_TOKENS) if (uses.get(token)! < uses.get(best)!) best = token
  return best
}
