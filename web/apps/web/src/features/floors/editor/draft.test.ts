import type { FloorDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import type { Point } from '../../../components/floorplan/geometry'
import {
  draftFromFloor,
  errorsFromProblem,
  floorRequest,
  layoutRequest,
  nextColorToken,
  nextRoomName,
  planSave,
  validateDraft,
  type Draft,
  type DraftRoom,
} from './draft'

const P = (x: number, y: number): Point => ({ x, y })
const square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

const floor: FloorDto = {
  id: 7,
  name: 'Ground',
  level: 0,
  widthCm: 2000,
  heightCm: 1500,
  gridCm: 50,
  background: null,
  rowVersion: 'AAAAAAAAB9E=',
  rooms: [
    {
      id: 11,
      name: 'Living',
      icon: 'sofa',
      colorToken: 'room-1',
      polygon: square(0, 0, 400),
      areaM2: 16,
      sortOrder: 0,
    },
    {
      id: 12,
      name: 'Kitchen',
      icon: 'cooking-pot',
      colorToken: 'room-2',
      polygon: square(400, 0, 300),
      areaM2: 9,
      sortOrder: 1,
    },
  ],
}

const room = (key: string, patch: Partial<DraftRoom> = {}): DraftRoom => ({
  key,
  name: key,
  icon: 'house',
  colorToken: 'room-3',
  polygon: square(800, 800, 100),
  ...patch,
})

describe('draftFromFloor / layoutRequest', () => {
  it('turns a floor into a draft and back into the layout request with the server ids', () => {
    const { draft, ids } = draftFromFloor(floor)
    expect(draft.floor).toEqual({ name: 'Ground', level: 0, widthCm: 2000, heightCm: 1500, gridCm: 50 })
    expect(draft.rooms.map((r) => r.name)).toEqual(['Living', 'Kitchen'])
    expect(draft.rooms[0]!.polygon).toEqual(square(0, 0, 400))

    const withNew: Draft = { ...draft, rooms: [...draft.rooms, room('new-1', { name: '  Office ' })] }
    expect(layoutRequest(withNew, ids, floor.rowVersion)).toEqual({
      rowVersion: floor.rowVersion,
      rooms: [
        { id: 11, name: 'Living', icon: 'sofa', colorToken: 'room-1', polygon: square(0, 0, 400) },
        { id: 12, name: 'Kitchen', icon: 'cooking-pot', colorToken: 'room-2', polygon: square(400, 0, 300) },
        { id: null, name: 'Office', icon: 'house', colorToken: 'room-3', polygon: square(800, 800, 100) },
      ],
    })
  })

  it('rounds coordinates to a tenth of a millimetre', () => {
    const { draft, ids } = draftFromFloor(floor)
    const odd: Draft = { ...draft, rooms: [room('x', { polygon: [P(0.123456, 1), P(100, 0), P(50, 50)] })] }
    expect(layoutRequest(odd, ids, 'rv').rooms[0]!.polygon[0]).toEqual(P(0.12, 1))
  })

  it('floorRequest sends the floor properties, the background and an optional size', () => {
    const { draft } = draftFromFloor(floor)
    expect(floorRequest(draft, 'rv')).toEqual({
      name: 'Ground',
      level: 0,
      widthCm: 2000,
      heightCm: 1500,
      gridCm: 50,
      background: null,
      rowVersion: 'rv',
    })
    expect(floorRequest(draft, 'rv', { widthCm: 2500, heightCm: 1500 }).widthCm).toBe(2500)
  })
})

describe('planSave', () => {
  const saved = draftFromFloor(floor).draft

  it('nothing changed: nothing to send', () => {
    expect(planSave(saved, saved)).toEqual([])
  })

  it('only rooms: the layout', () => {
    expect(planSave(saved, { ...saved, rooms: [] })).toEqual([{ kind: 'layout' }])
  })

  it('only the floor or its background: the floor', () => {
    const background = {
      fileId: 'f',
      opacity: 0.5,
      offsetXCm: 0,
      offsetYCm: 0,
      cmPerPx: 1,
    }
    expect(planSave(saved, { ...saved, background })).toEqual([
      { kind: 'floor', widthCm: 2000, heightCm: 1500 },
    ])
  })

  it('both: grows the floor first so rooms fit either size, then the layout, then the final size', () => {
    const draft: Draft = {
      ...saved,
      floor: { ...saved.floor, widthCm: 2500, heightCm: 1000 },
      rooms: [room('a', { polygon: square(2100, 0, 300) })],
    }
    expect(planSave(saved, draft)).toEqual([
      { kind: 'floor', widthCm: 2500, heightCm: 1500 },
      { kind: 'layout' },
      { kind: 'floor', widthCm: 2500, heightCm: 1000 },
    ])
  })

  it('both, floor only growing or unchanged in size: floor then layout', () => {
    const draft: Draft = { ...saved, floor: { ...saved.floor, name: 'Térreo' }, rooms: [] }
    expect(planSave(saved, draft)).toEqual([
      { kind: 'floor', widthCm: 2000, heightCm: 1500 },
      { kind: 'layout' },
    ])
  })
})

describe('validateDraft', () => {
  const base = draftFromFloor(floor).draft

  it('a valid draft has no errors', () => {
    expect(validateDraft(base).count).toBe(0)
  })

  it('flags rooms with the server geometry codes and missing names', () => {
    const draft: Draft = {
      ...base,
      rooms: [
        room('ok'),
        room('crossed', { polygon: [P(500, 0), P(800, 300), P(800, 0), P(500, 300)] }),
        room('outside', { polygon: square(1900, 0, 300) }),
        room('nameless', { name: '   ' }),
        room('long', { name: 'x'.repeat(81) }),
      ],
    }
    const errors = validateDraft(draft)
    expect(errors.count).toBe(4)
    expect(errors.rooms['ok']).toBeUndefined()
    expect(errors.rooms['crossed']).toEqual({ polygon: { key: 'errors.geometry.self_intersecting' } })
    expect(errors.rooms['outside']).toEqual({ polygon: { key: 'errors.geometry.out_of_bounds' } })
    expect(errors.rooms['nameless']).toEqual({ name: { key: 'validation.required' } })
    expect(errors.rooms['long']).toEqual({ name: { key: 'validation.maxLength', params: { max: 80 } } })
  })

  it('checks the floor properties against the server limits', () => {
    const errors = validateDraft({
      ...base,
      floor: { name: '', level: 101, widthCm: 99, heightCm: 100_001, gridCm: 0 },
      rooms: [],
    })
    expect(Object.keys(errors.floor).sort()).toEqual(['gridCm', 'heightCm', 'level', 'name', 'widthCm'])
    expect(errors.floor.name).toEqual({ key: 'validation.required' })
    expect(errors.count).toBe(5)
  })

  it('a smaller floor makes rooms that no longer fit out of bounds', () => {
    const errors = validateDraft({ ...base, floor: { ...base.floor, widthCm: 500 } })
    expect(errors.rooms[base.rooms[1]!.key]).toEqual({ polygon: { key: 'errors.geometry.out_of_bounds' } })
  })
})

describe('errorsFromProblem', () => {
  it('maps server field errors back to the rooms that were sent and the floor fields', () => {
    const errors = errorsFromProblem(
      {
        'rooms[1].polygon': ['geometry.zero_area'],
        'rooms[0].name': ["'Name' must not be empty."],
        'rooms[1].id': ['rooms.not_found'],
        widthCm: ['geometry.out_of_bounds'],
        'background.fileId': ['files.not_found'],
        'rooms[9].polygon': ['geometry.zero_area'],
      },
      ['a', 'b'],
    )
    expect(errors.rooms).toEqual({
      a: { name: { key: 'validation.invalid' } },
      b: { polygon: { key: 'errors.geometry.zero_area' }, other: { key: 'errors.rooms.not_found' } },
    })
    expect(errors.floor).toEqual({
      widthCm: { key: 'errors.geometry.out_of_bounds' },
      background: { key: 'errors.files.not_found' },
    })
    expect(errors.count).toBe(5)
  })
})

describe('naming and colors of new rooms', () => {
  it('numbers new rooms after the existing ones, skipping taken names', () => {
    expect(nextRoomName([], 'Room')).toBe('Room 1')
    expect(nextRoomName([room('a'), room('b', { name: 'Room 2' })], 'Room')).toBe('Room 3')
  })

  it('picks the least used room color, in palette order', () => {
    expect(nextColorToken([])).toBe('room-1')
    expect(nextColorToken([room('a', { colorToken: 'room-1' }), room('b', { colorToken: 'room-2' })])).toBe(
      'room-3',
    )
  })
})
