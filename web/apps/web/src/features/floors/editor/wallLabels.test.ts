import { describe, expect, it } from 'vitest'
import type { Point } from '../../../components/floorplan/geometry'
import { wallLabels, type WallLabelOptions } from './wallLabels'

// At 1 px per cm, so screen sizes and floor positions coincide.

const P = (x: number, y: number): Point => ({ x, y })
const rect = (x0: number, y0: number, x1: number, y1: number): Point[] => [
  P(x0, y0),
  P(x1, y0),
  P(x1, y1),
  P(x0, y1),
]

const options = (patch: Partial<WallLabelOptions> = {}): WallLabelOptions => ({
  others: [],
  widthCm: 2000,
  heightCm: 1500,
  scale: 1,
  minWallPx: 72,
  gapPx: 8,
  text: (cm) => `${cm / 100} m`,
  ...patch,
})

describe('wallLabels', () => {
  it('puts each wall length outside its wall, clear of the midpoint handle', () => {
    const labels = wallLabels(rect(200, 200, 600, 500), options())
    expect(labels.map((label) => label.text)).toEqual(['4 m', '3 m', '4 m', '3 m'])
    expect(labels.every((label) => !label.inside)).toBe(true)
    // Top wall: the pill (18 px tall) sits gap + half its height above the wall.
    expect(labels[0]!.at).toEqual(P(400, 200 - 8 - 9))
    expect(labels[1]!.at.x).toBeGreaterThan(600 + 8)
    expect(labels[3]!.at.x).toBeLessThan(200 - 8)
  })

  it("moves a label inside the room when a neighbour's room is on the other side of the wall", () => {
    const labels = wallLabels(rect(200, 200, 600, 500), options({ others: [rect(600, 200, 900, 500)] }))
    const right = labels.find((label) => label.index === 1)!
    expect(right.inside).toBe(true)
    expect(right.at.x).toBeLessThan(600 - 8)
    expect(labels.filter((label) => label.inside)).toHaveLength(1)
  })

  it('moves a label inside the room at the edge of the floor', () => {
    const labels = wallLabels(rect(0, 0, 400, 300), options())
    const top = labels.find((label) => label.index === 0)!
    expect(top).toMatchObject({ inside: true, at: P(200, 8 + 9) })
    expect(labels.find((label) => label.index === 3)!.inside).toBe(true)
    expect(labels.find((label) => label.index === 1)!.inside).toBe(false)
  })

  it('skips walls too short on screen to carry a label', () => {
    const labels = wallLabels(rect(200, 200, 600, 250), options({ scale: 1 }))
    expect(labels.map((label) => label.index)).toEqual([0, 2])
    expect(wallLabels(rect(200, 200, 600, 500), options({ scale: 0.1 }))).toEqual([])
  })

  it("drops a label that would cover a room's own label (name and area)", () => {
    // The room's label in the middle: 120 × 50 around (400, 350). Inside pills on the left and right walls would hit
    // it once the room is narrow; outside pills never do.
    const avoid = [{ minX: 340, maxX: 460, minY: 325, maxY: 375 }]
    const narrow = wallLabels(
      rect(300, 200, 500, 500),
      options({ others: [rect(500, 200, 800, 500)], avoid }),
    )
    expect(narrow.find((label) => label.index === 1)).toBeUndefined()
    expect(narrow.find((label) => label.index === 3)).toMatchObject({ inside: false })
  })

  it('drops a label that would cover one already placed', () => {
    // A small room in the corner of the floor: both pills go inside, and they would collide.
    const labels = wallLabels(rect(0, 0, 80, 80), options({ text: () => 'a long label' }))
    const boxes = labels.map((label) => label.index)
    expect(boxes.length).toBeLessThan(4)
    expect(boxes).toContain(0)
  })
})
