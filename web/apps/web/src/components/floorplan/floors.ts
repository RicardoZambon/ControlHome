import { getFloor, listFloors, type FloorDto, type FloorSummaryDto } from '@controlhome/api-client'
import { roomAreaM2 } from './measure'
import { unwrap, type Translate } from '../../lib/problem'

/** "Ground floor", "Level 2", "Basement 1". */
export function levelLabel(level: number, t: Translate): string {
  if (level === 0) return t('floors.level.ground')
  return level > 0 ? t('floors.level.above', { n: level }) : t('floors.level.below', { n: -level })
}

/**
 * The small label above a floor's name: its level ("Floor 1"), or "Level 0" when the name already says the level
 * (a floor named "Ground floor"), so the two lines never repeat each other.
 */
export function floorEyebrow(floor: Pick<FloorSummaryDto, 'name' | 'level'>, t: Translate): string {
  const label = levelLabel(floor.level, t)
  const same = label.trim().toLocaleLowerCase() === floor.name.trim().toLocaleLowerCase()
  return same ? t('floors.level.number', { n: floor.level }) : label
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
