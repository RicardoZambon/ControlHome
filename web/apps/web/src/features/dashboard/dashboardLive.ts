import type { DashboardActivityDto, DashboardRoomDto, StateValue } from '@controlhome/api-client'
import { asNumber, sameState } from '../../lib/stateValue'

/**
 * The dashboard summary kept live (docs/architecture.md §3 "Dashboard summary"): `GET /api/dashboard` lists, for each
 * aggregate, the attribute ids it is computed from, so the page recomputes it from the hub's current values. When no
 * live value is known yet for an aggregate, the server's figure stands. What a device that went offline last said
 * (`excluded`: its attribute ids) does not count: a washer that stopped answering draws no power and runs no cycle here.
 */

/** Reads the current value of an attribute (the realtime store). */
export type ValueReader = (attributeId: number) => StateValue | undefined

const NONE: ReadonlySet<number> = new Set()

function numbers(ids: readonly number[], read: ValueReader): number[] {
  return ids.map((id) => asNumber(read(id))).filter((value): value is number => value !== undefined)
}

/** The ids still counted; `null` when every one of them is excluded (nothing left to show). */
function counted(ids: readonly number[], excluded: ReadonlySet<number>): number[] | null {
  if (excluded.size === 0) return [...ids]
  const kept = ids.filter((id) => !excluded.has(id))
  return ids.length > 0 && kept.length === 0 ? null : kept
}

/** The sum of the readings (one decimal), or `fallback` when none is known. */
export function liveSum(
  ids: readonly number[],
  read: ValueReader,
  fallback: number | null,
  excluded: ReadonlySet<number> = NONE,
): number | null {
  const kept = counted(ids, excluded)
  if (kept === null) return null
  const values = numbers(kept, read)
  if (values.length === 0) return excluded.size > 0 && kept.length < ids.length ? null : fallback
  return Math.round(values.reduce((sum, value) => sum + value, 0) * 10) / 10
}

/** The mean of the readings (one decimal), or `fallback` when none is known. */
export function liveMean(
  ids: readonly number[],
  read: ValueReader,
  fallback: number | null,
  excluded: ReadonlySet<number> = NONE,
): number | null {
  const kept = counted(ids, excluded)
  if (kept === null) return null
  const values = numbers(kept, read)
  if (values.length === 0) return excluded.size > 0 && kept.length < ids.length ? null : fallback
  return Math.round((values.reduce((sum, value) => sum + value, 0) / values.length) * 10) / 10
}

/** How many devices have something on: a device counts once, however many of its attributes are active. */
export function liveActive(
  activities: readonly DashboardActivityDto[],
  read: ValueReader,
  fallback: number,
  excluded: ReadonlySet<number> = NONE,
): number {
  let known = false
  const active = new Set<string>()
  for (const activity of activities) {
    if (excluded.has(activity.attributeId)) {
      known = true
      continue
    }
    const value = read(activity.attributeId)
    if (value === undefined) continue
    known = true
    if (activity.activeValues.some((candidate) => sameState(candidate, value))) active.add(activity.deviceId)
  }
  return known ? active.size : fallback
}

export interface RoomLive {
  activeDeviceCount: number
  temperatureC: number | null
  powerW: number | null
}

export function roomLive(
  room: DashboardRoomDto,
  read: ValueReader,
  excluded: ReadonlySet<number> = NONE,
): RoomLive {
  return {
    activeDeviceCount: liveActive(room.activityAttributes, read, room.activeDeviceCount, excluded),
    temperatureC: liveMean(room.temperatureAttributeIds, read, room.temperatureC, excluded),
    powerW: liveSum(room.powerAttributeIds, read, room.powerW, excluded),
  }
}

/** Watts as shown: whole watts below 1 kW ("640 W"), kilowatts with two decimals above ("1,25 kW"). */
export function formatPower(watts: number, locale: string): { value: string; unit: 'W' | 'kW' } {
  if (Math.abs(watts) >= 1000) {
    return {
      value: new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
        watts / 1000,
      ),
      unit: 'kW',
    }
  }
  return {
    value: new Intl.NumberFormat(locale, { maximumFractionDigits: Math.abs(watts) < 10 ? 1 : 0 }).format(
      watts,
    ),
    unit: 'W',
  }
}

/** How the home's setup is going: what the "get started" steps show as done. */
export interface SetupProgress {
  floors: boolean
  devices: boolean
  placed: boolean
  done: boolean
}

export function setupProgress(summary: {
  floorCount: number
  deviceCount: number
  placements: number
}): SetupProgress {
  const floors = summary.floorCount > 0
  const devices = summary.deviceCount > 0
  const placed = summary.placements > 0
  return { floors, devices, placed, done: floors && devices }
}
