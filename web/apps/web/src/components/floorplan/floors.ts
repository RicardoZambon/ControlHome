import { getFloor, listFloors, type FloorDto, type FloorSummaryDto } from '@controlhome/api-client'
import { roomAreaM2 } from './measure'
import { unwrap, type Translate } from '../../lib/problem'

/** "Ground floor", "Level 2", "Basement 1". */
export function levelLabel(level: number, t: Translate): string {
  if (level === 0) return t('floors.level.ground')
  return level > 0 ? t('floors.level.above', { n: level }) : t('floors.level.below', { n: -level })
}

/**
 * The small label above a floor's name: its level in words ("Floor 1", "Basement 1"), or nothing when the name already
 * says it (a floor named "Ground floor"), so the two lines never repeat each other.
 */
export function floorEyebrow(floor: Pick<FloorSummaryDto, 'name' | 'level'>, t: Translate): string {
  const label = levelLabel(floor.level, t)
  const same = label.trim().toLocaleLowerCase() === floor.name.trim().toLocaleLowerCase()
  return same ? '' : label
}

/** How much room is left around what a plan shows when it is fitted to it (cm). */
const PLAN_MARGIN_CM = 60

/**
 * What is drawn on a floor — its rooms and its devices — as a rectangle in cm, with a margin, inside the floor: a plan
 * opens fitted to it rather than to the whole (mostly empty) floor sheet. The whole floor when it has no rooms.
 */
export function planBounds(floor: {
  widthCm: number
  heightCm: number
  rooms: readonly { polygon: readonly { x: number; y: number }[] }[]
  placements: readonly { xCm: number; yCm: number }[]
}): { x: number; y: number; width: number; height: number } {
  const points = [
    ...floor.rooms.flatMap((room) => room.polygon),
    ...floor.placements.map((placement) => ({ x: placement.xCm, y: placement.yCm })),
  ]
  if (floor.rooms.length === 0 || points.length === 0)
    return { x: 0, y: 0, width: floor.widthCm, height: floor.heightCm }
  const minX = Math.max(0, Math.min(...points.map((p) => p.x)) - PLAN_MARGIN_CM)
  const minY = Math.max(0, Math.min(...points.map((p) => p.y)) - PLAN_MARGIN_CM)
  const maxX = Math.min(floor.widthCm, Math.max(...points.map((p) => p.x)) + PLAN_MARGIN_CM)
  const maxY = Math.min(floor.heightCm, Math.max(...points.map((p) => p.y)) + PLAN_MARGIN_CM)
  return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) }
}

/** The rooms' total area in m². */
export function totalAreaM2(floor: Pick<FloorDto, 'rooms'>): number {
  return floor.rooms.reduce((sum, room) => sum + roomAreaM2(room.polygon), 0)
}

/** Floors as the API lists them: by level, then by id. */
export function sortFloors<T extends Pick<FloorSummaryDto, 'id' | 'level'>>(floors: readonly T[]): T[] {
  return [...floors].sort((a, b) => a.level - b.level || a.id - b.id)
}

/** Every floor with its rooms (one request per floor: a home has a handful). */
export async function loadFloorsWithRooms(): Promise<FloorDto[]> {
  const summaries = sortFloors(unwrap(await listFloors()))
  return Promise.all(summaries.map(async (floor) => unwrap(await getFloor({ path: { id: floor.id } }))))
}

/** The floor shown first (dashboard): the ground floor if there is one, else the lowest above it, else the first. */
export function primaryFloor<T extends Pick<FloorSummaryDto, 'id' | 'level'>>(
  floors: readonly T[],
): T | undefined {
  const sorted = sortFloors(floors)
  return sorted.find((floor) => floor.level >= 0) ?? sorted[0]
}
