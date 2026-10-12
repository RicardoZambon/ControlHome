import { describe, expect, it } from 'vitest'
import type { Point } from '../../../../components/floorplan/geometry'
import {
  addDrawPoint,
  canCloseDraw,
  drawSegmentCrosses,
  emptyDraw,
  rectanglePolygon,
  removeLastDrawPoint,
  type DrawState,
} from './drawTool'

const P = (x: number, y: number): Point => ({ x, y })
const placed = (...points: Point[]): DrawState => ({ points })

describe('draw tool', () => {
  it('each click adds a corner', () => {
    let state = emptyDraw()
    for (const point of [P(0, 0), P(100, 0), P(100, 100)]) state = addDrawPoint(state, point, 10).state
    expect(state.points).toEqual([P(0, 0), P(100, 0), P(100, 100)])
  })

  it('clicking the first corner closes the outline once there are three corners', () => {
    const state = placed(P(0, 0), P(100, 0), P(100, 100))
    const result = addDrawPoint(state, P(4, 3), 10)
    expect(result.closed).toEqual([P(0, 0), P(100, 0), P(100, 100)])
    expect(result.state.points).toEqual([])
  })

  it('with fewer than three corners the first corner does not close', () => {
    const result = addDrawPoint(placed(P(0, 0), P(100, 0)), P(2, 2), 10)
    expect(result.closed).toBeNull()
    // A click on top of an existing corner adds nothing.
    expect(result.state.points).toEqual([P(0, 0), P(100, 0)])
  })

  it('a second click on the last corner (a double click) adds nothing', () => {
    const state = placed(P(0, 0), P(100, 0))
    expect(addDrawPoint(state, P(100.2, 0.1), 10).state).toBe(state)
  })

  it('canCloseDraw needs three distinct corners', () => {
    expect(canCloseDraw(placed(P(0, 0), P(100, 0)))).toBe(false)
    expect(canCloseDraw(placed(P(0, 0), P(100, 0), P(100, 100)))).toBe(true)
  })

  it('Backspace removes the last corner', () => {
    expect(removeLastDrawPoint(placed(P(0, 0), P(100, 0))).points).toEqual([P(0, 0)])
    expect(removeLastDrawPoint(emptyDraw()).points).toEqual([])
  })

  it('flags a next segment that would cross the outline drawn so far', () => {
    const state = placed(P(0, 0), P(200, 0), P(200, 200), P(100, -100))
    // From (100, -100) down to (100, 100) crosses the first wall (0,0)-(200,0).
    expect(drawSegmentCrosses(state.points, P(100, 100))).toBe(true)
    expect(drawSegmentCrosses(placed(P(0, 0), P(200, 0), P(200, 200)).points, P(0, 200))).toBe(false)
    expect(drawSegmentCrosses([], P(0, 0))).toBe(false)
  })

  it('a dragged rectangle becomes four corners', () => {
    expect(rectanglePolygon(P(300, 200), P(100, 50))).toEqual([
      P(100, 50),
      P(300, 50),
      P(300, 200),
      P(100, 200),
    ])
  })
})
