import { describe, expect, it } from 'vitest'
import type { Point } from '../../../../components/floorplan/geometry'
import {
  clampMove,
  clampToFloor,
  exceedsDragThreshold,
  hitTest,
  insertVertex,
  midpointHit,
  moveVertex,
  removeVertex,
  translatePolygon,
} from './selectTool'

const P = (x: number, y: number): Point => ({ x, y })
const square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

const rooms = [
  { key: 'a', polygon: square(0, 0, 200) },
  { key: 'b', polygon: square(150, 150, 200) }, // overlaps a, drawn above it
]

describe('hitTest', () => {
  it('finds the vertex handles of the selected room first', () => {
    expect(hitTest(rooms, P(197, 3), 10, 'a')).toEqual({ kind: 'vertex', roomKey: 'a', index: 1 })
  })

  it('does not offer vertices of rooms that are not selected', () => {
    expect(hitTest(rooms, P(197, 3), 10, null)).toEqual({ kind: 'room', roomKey: 'a' })
  })

  it('finds an edge of the selected room with the point on it', () => {
    expect(hitTest(rooms, P(100, 4), 10, 'a')).toEqual({
      kind: 'edge',
      roomKey: 'a',
      index: 0,
      point: P(100, 0),
    })
  })

  it('picks the topmost room under the point', () => {
    expect(hitTest(rooms, P(175, 175), 1, null)).toEqual({ kind: 'room', roomKey: 'b' })
    expect(hitTest(rooms, P(50, 50), 1, null)).toEqual({ kind: 'room', roomKey: 'a' })
  })

  it('the selected room wins inside its own outline even below another', () => {
    expect(hitTest(rooms, P(175, 175), 1, 'a')).toEqual({ kind: 'room', roomKey: 'a' })
  })

  it('a point just outside a thin room still grabs it', () => {
    expect(hitTest([{ key: 'c', polygon: square(500, 500, 10) }], P(513, 505), 5, null)).toEqual({
      kind: 'room',
      roomKey: 'c',
    })
  })

  it('nothing under the point', () => {
    expect(hitTest(rooms, P(900, 900), 10, 'a')).toEqual({ kind: 'none' })
  })
})

describe('midpointHit', () => {
  it('finds the middle of a wall of the selected room (a handle that adds a corner)', () => {
    expect(midpointHit(square(0, 0, 200), P(98, 3), 8)).toEqual({ index: 0, point: P(100, 0) })
    expect(midpointHit(square(0, 0, 200), P(0, 103), 8)).toEqual({ index: 3, point: P(0, 100) })
    expect(midpointHit(square(0, 0, 200), P(50, 50), 8)).toBeNull()
  })
})

describe('polygon edits', () => {
  const polygon = square(0, 0, 100)

  it('moves, inserts and removes vertices without touching the original', () => {
    expect(moveVertex(polygon, 2, P(120, 130))).toEqual([P(0, 0), P(100, 0), P(120, 130), P(0, 100)])
    expect(insertVertex(polygon, 0, P(50, -10))).toEqual([
      P(0, 0),
      P(50, -10),
      P(100, 0),
      P(100, 100),
      P(0, 100),
    ])
    expect(insertVertex(polygon, 3, P(-10, 50))).toEqual([...polygon, P(-10, 50)])
    expect(removeVertex(polygon, 1)).toEqual([P(0, 0), P(100, 100), P(0, 100)])
    expect(polygon).toEqual(square(0, 0, 100))
  })

  it('never removes a vertex from a triangle', () => {
    expect(removeVertex([P(0, 0), P(100, 0), P(0, 100)], 0)).toBeNull()
  })

  it('translates', () => {
    expect(translatePolygon(polygon, 10, -5)).toEqual(square(10, -5, 100).map((p) => p))
  })
})

describe('keeping edits on the floor', () => {
  it('clampMove stops a dragged room at the floor edges', () => {
    const polygon = square(100, 100, 200)
    expect(clampMove(polygon, -150, 50, 1000, 800)).toEqual({ dx: -100, dy: 50 })
    expect(clampMove(polygon, 900, 900, 1000, 800)).toEqual({ dx: 700, dy: 500 })
  })

  it('clampToFloor keeps a dragged vertex inside', () => {
    expect(clampToFloor(P(-5, 900), 1000, 800)).toEqual(P(0, 800))
    expect(clampToFloor(P(10, 20), 1000, 800)).toEqual(P(10, 20))
  })
})

describe('exceedsDragThreshold', () => {
  it('starts a drag after the pointer moved more than 4 px', () => {
    expect(exceedsDragThreshold(P(10, 10), P(12, 13))).toBe(false)
    expect(exceedsDragThreshold(P(10, 10), P(15, 10))).toBe(true)
  })
})
