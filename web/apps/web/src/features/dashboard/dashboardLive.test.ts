import type { DashboardRoomDto, StateValue } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import { bool, num, str } from '../../test/devices'
import { formatPower, liveActive, liveMean, liveSum, roomLive, setupProgress } from './dashboardLive'

const reader = (values: Record<number, StateValue>) => (id: number) => values[id]

const ROOM: DashboardRoomDto = {
  id: 10,
  name: 'Living room',
  icon: 'sofa',
  colorToken: 'room-1',
  floorId: 1,
  floorName: 'Ground floor',
  deviceCount: 3,
  activeDeviceCount: 1,
  temperatureC: 24,
  powerW: 60,
  temperatureAttributeIds: [11, 12],
  powerAttributeIds: [4, 5],
  activityAttributes: [
    { deviceId: 'd-light', attributeId: 1, activeValues: [bool(true)] },
    { deviceId: 'd-tv', attributeId: 2, activeValues: [bool(true)] },
    { deviceId: 'd-washer', attributeId: 3, activeValues: [str('running'), str('paused')] },
    { deviceId: 'd-washer', attributeId: 6, activeValues: [bool(true)] },
  ],
}

describe('live aggregates', () => {
  it('sums and averages the known readings, else keeps the server figure', () => {
    expect(liveSum([4, 5, 9], reader({ 4: num(12.04), 5: num(100) }), null)).toBe(112)
    expect(liveSum([4], reader({}), 60)).toBe(60)
    expect(liveMean([11, 12], reader({ 11: num(22), 12: num(23.5) }), null)).toBe(22.8)
    expect(liveMean([11], reader({ 11: str('n/a') }), 24)).toBe(24)
  })

  it('counts each active device once', () => {
    const read = reader({ 1: bool(true), 2: bool(false), 3: str('running'), 6: bool(true) })
    expect(liveActive(ROOM.activityAttributes, read, 0)).toBe(2)
    expect(liveActive(ROOM.activityAttributes, reader({}), 7)).toBe(7)
  })

  it('recomputes a room from the live values', () => {
    expect(roomLive(ROOM, reader({ 1: bool(false), 11: num(21), 4: num(5) }))).toEqual({
      activeDeviceCount: 0,
      temperatureC: 21,
      powerW: 5,
    })
  })

  it('leaves out what offline devices last said: their power, their temperature, being on', () => {
    const read = reader({
      1: bool(true),
      3: str('running'),
      4: num(5),
      5: num(428),
      11: num(21),
      12: num(30),
    })
    // The washer (attributes 3, 5 and 12 here) stopped answering.
    expect(roomLive(ROOM, read, new Set([3, 5, 6, 12]))).toEqual({
      activeDeviceCount: 1,
      temperatureC: 21,
      powerW: 5,
    })
    // Only offline readings: nothing to show (not the server's figure, which still has them).
    expect(liveSum([5], read, 428, new Set([5]))).toBeNull()
    expect(liveActive([ROOM.activityAttributes[2]!], read, 1, new Set([3]))).toBe(0)
  })
})

describe('formatPower', () => {
  it('shows watts below 1 kW and kilowatts above', () => {
    expect(formatPower(640.4, 'en')).toEqual({ value: '640', unit: 'W' })
    expect(formatPower(4.25, 'en')).toEqual({ value: '4.3', unit: 'W' })
    expect(formatPower(1250, 'pt-BR')).toEqual({ value: '1,25', unit: 'kW' })
  })
})

describe('setupProgress', () => {
  it('is done once the home has floors and devices', () => {
    expect(setupProgress({ floorCount: 0, deviceCount: 3, placements: 0 })).toEqual({
      floors: false,
      devices: true,
      placed: false,
      done: false,
    })
    expect(setupProgress({ floorCount: 2, deviceCount: 3, placements: 0 }).done).toBe(true)
  })
})
