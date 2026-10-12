import { describe, expect, it } from 'vitest'
import type { Point } from '../../../../components/floorplan/geometry'
import { rectangleOf, resizeRectangle, sizeOf } from './dimensions'

const P = (x: number, y: number): Point => ({ x, y })

describe('rectangleOf', () => {
  it('reads an axis-aligned rectangle, whatever corner it starts at and whichever way it turns', () => {
    const rect = { x: 100, y: 50, width: 400, height: 300 }
    expect(rectangleOf([P(100, 50), P(500, 50), P(500, 350), P(100, 350)])).toEqual(rect)
    expect(rectangleOf([P(500, 350), P(500, 50), P(100, 50), P(100, 350)])).toEqual(rect)
  })

  it('is null for anything else: an L-shape, a slanted wall, a rotated square, a sliver', () => {
    expect(rectangleOf([P(0, 0), P(400, 0), P(400, 200), P(200, 200), P(200, 400), P(0, 400)])).toBeNull()
    expect(rectangleOf([P(0, 0), P(400, 0), P(450, 300), P(0, 300)])).toBeNull()
    expect(rectangleOf([P(200, 0), P(400, 200), P(200, 400), P(0, 200)])).toBeNull()
    expect(rectangleOf([P(0, 0), P(400, 0), P(400, 0), P(0, 0)])).toBeNull()
    expect(rectangleOf([P(0, 0), P(400, 0), P(400, 300)])).toBeNull()
  })
})

describe('sizeOf', () => {
  it('is the width and depth of the box around the room', () => {
    expect(sizeOf([P(0, 0), P(400, 0), P(400, 200), P(200, 200), P(200, 450), P(0, 450)])).toEqual({
      width: 400,
      height: 450,
    })
  })
})

describe('resizeRectangle', () => {
  it('moves and resizes a rectangle, keeping its corner order', () => {
    const polygon = [P(500, 350), P(500, 50), P(100, 50), P(100, 350)]
    expect(resizeRectangle(polygon, { x: 100, y: 50, width: 600, height: 300 })).toEqual([
      P(700, 350),
      P(700, 50),
      P(100, 50),
      P(100, 350),
    ])
    expect(resizeRectangle(polygon, { x: 0, y: 0, width: 400, height: 250 })).toEqual([
      P(400, 250),
      P(400, 0),
      P(0, 0),
      P(0, 250),
    ])
  })

  it('leaves other shapes alone (null)', () => {
    const triangle = [P(0, 0), P(400, 0), P(0, 300)]
    expect(resizeRectangle(triangle, { x: 0, y: 0, width: 100, height: 100 })).toBeNull()
  })
})
