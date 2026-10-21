import { describe, expect, it } from 'vitest'
import { floorEyebrow, levelLabel, planBounds, primaryFloor, sortFloors, totalAreaM2 } from './floors'

const t = (key: string, named?: Record<string, unknown>) => `${key}${named ? JSON.stringify(named) : ''}`

describe('levelLabel', () => {
  it('names the ground floor, the floors above and the basements', () => {
    expect(levelLabel(0, t)).toBe('floors.level.ground')
    expect(levelLabel(2, t)).toBe('floors.level.above{"n":2}')
    expect(levelLabel(-1, t)).toBe('floors.level.below{"n":1}')
  })
})

describe('floorEyebrow', () => {
  const named = (key: string, params?: Record<string, unknown>) =>
    key === 'floors.level.ground'
      ? 'Ground floor'
      : key === 'floors.level.number'
        ? `Level ${String(params?.n)}`
        : key

  it('is the friendly level, or nothing when the name already says it', () => {
    expect(floorEyebrow({ name: 'Térreo', level: 0 }, named)).toBe('Ground floor')
    expect(floorEyebrow({ name: ' ground FLOOR ', level: 0 }, named)).toBe('')
  })
})

describe('sortFloors / primaryFloor', () => {
  const floors = [
    { id: 3, level: 1 },
    { id: 1, level: -1 },
    { id: 2, level: 0 },
    { id: 4, level: 0 },
  ]

  it('orders by level, then id', () => {
    expect(sortFloors(floors).map((f) => f.id)).toEqual([1, 2, 4, 3])
  })

  it('shows the ground floor first, else the lowest floor', () => {
    expect(primaryFloor(floors)?.id).toBe(2)
    expect(primaryFloor([{ id: 9, level: -2 }])?.id).toBe(9)
    expect(primaryFloor([])).toBeUndefined()
  })
})

describe('totalAreaM2', () => {
  it('adds the rooms up', () => {
    const square = (size: number) => [
      { x: 0, y: 0 },
      { x: size, y: 0 },
      { x: size, y: size },
      { x: 0, y: size },
    ]
    expect(totalAreaM2({ rooms: [{ polygon: square(300) }, { polygon: square(100) }] as never })).toBe(10)
  })
})

describe('planBounds', () => {
  const rect = (x: number, y: number, w: number, h: number) => [
    { x, y },
    { x: x + w, y },
    { x: x + w, y: y + h },
    { x, y: y + h },
  ]
  const floor = { widthCm: 1600, heightCm: 1200 }

  it('is what is drawn — the rooms and the devices — with a margin, inside the floor', () => {
    const bounds = planBounds({
      ...floor,
      rooms: [{ polygon: rect(100, 100, 500, 500) }, { polygon: rect(600, 100, 900, 900) }],
      placements: [{ xCm: 50, yCm: 1050 }],
    })
    expect(bounds).toEqual({ x: 0, y: 40, width: 1560, height: 1070 })
  })

  it('is the whole floor without rooms', () => {
    expect(planBounds({ ...floor, rooms: [], placements: [] })).toEqual({
      x: 0,
      y: 0,
      width: 1600,
      height: 1200,
    })
  })
})
