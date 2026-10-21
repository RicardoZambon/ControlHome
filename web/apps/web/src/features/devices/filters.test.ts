import { describe, expect, it } from 'vitest'
import type { EndpointEntry } from '../../stores/devices'
import { ROOMS, basicHome } from '../../test/devices'
import { NO_ROOM, filterEntries, groupByRoom, normalizeText } from './filters'

function entries(): EndpointEntry[] {
  return basicHome().flatMap((device) =>
    device.endpoints.map((endpoint) => ({
      device,
      endpoint,
      roomId: endpoint.effectiveRoomId ?? device.roomId,
    })),
  )
}

const roomName = (id: number) => ROOMS.find((room) => room.id === id)?.name
const names = (list: EndpointEntry[]) => list.map((entry) => entry.device.name)

describe('filterEntries', () => {
  it('filters by room, or devices without one', () => {
    expect(names(filterEntries(entries(), { room: '12' }, { includeHidden: false, roomName }))).toEqual([
      'Bedroom air conditioner',
    ])
    expect(names(filterEntries(entries(), { room: NO_ROOM }, { includeHidden: false, roomName }))).toEqual([
      'Front door sensor',
      'Front door lock',
    ])
  })

  it('filters by capability and integration', () => {
    expect(
      names(filterEntries(entries(), { capability: 'switch' }, { includeHidden: false, roomName })),
    ).toEqual(['Living room light', 'Coffee maker plug', 'Bedroom air conditioner'])
    expect(filterEntries(entries(), { integration: 'other' }, { includeHidden: false, roomName })).toEqual([])
  })

  it('searches names and rooms, ignoring case and accents', () => {
    expect(names(filterEntries(entries(), { q: 'FRONT lock' }, { includeHidden: false, roomName }))).toEqual([
      'Front door lock',
    ])
    expect(names(filterEntries(entries(), { q: 'kitchen' }, { includeHidden: false, roomName }))).toEqual([
      'Coffee maker plug',
    ])
    expect(normalizeText('Cômodo São')).toBe('comodo sao')
  })

  it('leaves hidden endpoints out unless asked', () => {
    const list = entries()
    list[0] = { ...list[0]!, endpoint: { ...list[0]!.endpoint, hidden: true } }
    expect(filterEntries(list, {}, { includeHidden: false, roomName })).toHaveLength(4)
    expect(filterEntries(list, {}, { includeHidden: true, roomName })).toHaveLength(5)
  })
})

describe('groupByRoom', () => {
  it('groups in the rooms order, devices without a (known) room last', () => {
    const groups = groupByRoom(entries(), ROOMS)
    expect(groups.map((group) => group.room?.name ?? null)).toEqual([
      'Living room',
      'Kitchen',
      'Bedroom',
      null,
    ])
    expect(groups.at(-1)!.entries).toHaveLength(2)
    // A room that no longer exists: its devices count as without a room.
    expect(groupByRoom(entries(), ROOMS.slice(1)).at(-1)!.entries).toHaveLength(3)
  })
})
