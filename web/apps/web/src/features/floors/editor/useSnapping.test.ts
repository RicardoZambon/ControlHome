import { describe, expect, it } from 'vitest'
import type { Point } from '../../../components/floorplan/geometry'
import { snapContext, snapMove, snapPoint, useSnapping, type SnapContext } from './useSnapping'

const P = (x: number, y: number): Point => ({ x, y })
const square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

const empty: SnapContext = { gridCm: 50, vertices: [], edges: [] }

describe('snapPoint', () => {
  it('snaps to a grid intersection within the threshold', () => {
    expect(snapPoint(P(104, 147), empty, 10)).toEqual({ point: P(100, 150), kind: 'grid' })
  })

  it('snaps each axis to its grid line on its own', () => {
    // x is 4 cm from a line, y is 20 cm from the nearest one.
    expect(snapPoint(P(104, 130), empty, 10)).toEqual({ point: P(100, 130), kind: 'grid' })
  })

  it('leaves a point far from everything alone', () => {
    expect(snapPoint(P(120, 130), empty, 10)).toEqual({ point: P(120, 130), kind: null })
  })

  it('snaps to another room vertex before a farther grid line', () => {
    const context: SnapContext = { gridCm: 50, vertices: [P(127, 133)], edges: [] }
    expect(snapPoint(P(124, 131), context, 10)).toEqual({ point: P(127, 133), kind: 'vertex' })
  })

  it('the closest candidate wins', () => {
    // The grid line x = 100 is 2 cm away, the vertex 5 cm.
    const context: SnapContext = { gridCm: 50, vertices: [P(105, 123)], edges: [] }
    expect(snapPoint(P(102, 120), context, 10)).toEqual({ point: P(100, 120), kind: 'grid' })
  })

  it('snaps onto another room edge', () => {
    const context = snapContext([{ key: 'b', polygon: square(200, 0, 300) }], { gridCm: 1000 })
    expect(snapPoint(P(196, 230), context, 10)).toEqual({ point: P(200, 230), kind: 'edge' })
  })

  it('prefers the vertex over the edge near a corner', () => {
    const context = snapContext([{ key: 'b', polygon: square(200, 0, 300) }], { gridCm: 1000 })
    expect(snapPoint(P(203, 296), context, 10)).toEqual({ point: P(200, 300), kind: 'vertex' })
  })

  it('does nothing when disabled (Alt held)', () => {
    expect(snapPoint(P(104, 147), empty, 10, true)).toEqual({ point: P(104, 147), kind: null })
  })
})

describe('snapContext', () => {
  it('collects the vertices and edges of the other rooms and the own extra vertices', () => {
    const context = snapContext(
      [
        { key: 'a', polygon: square(0, 0, 100) },
        { key: 'b', polygon: square(200, 0, 100) },
      ],
      { gridCm: 25, excludeKey: 'a', ownVertices: [P(5, 5)] },
    )
    expect(context.gridCm).toBe(25)
    expect(context.vertices).toEqual([...square(200, 0, 100), P(5, 5)])
    expect(context.edges).toHaveLength(4)
  })
})

describe('snapMove', () => {
  it('shifts a dragged room so its nearest vertex lands on a snap target', () => {
    const context = snapContext([{ key: 'b', polygon: square(300, 0, 100) }], { gridCm: 1000 })
    // Moved so its right edge is at x = 296: 4 cm short of the other room's left wall.
    const moved = square(196, 0, 100)
    expect(snapMove(moved, context, 10)).toEqual({ dx: 4, dy: 0, kind: 'vertex' })
  })

  it('a vertex near another room wall slides onto it', () => {
    const context = snapContext([{ key: 'b', polygon: square(300, 0, 400) }], { gridCm: 1000 })
    expect(snapMove(square(195, 150, 100), context, 10)).toEqual({ dx: 5, dy: 0, kind: 'edge' })
  })

  it('otherwise puts the first vertex on the grid', () => {
    expect(snapMove(square(103, 98, 100), empty, 5)).toEqual({ dx: -3, dy: 2, kind: 'grid' })
  })

  it('no snap: no shift', () => {
    expect(snapMove(square(120, 120, 10), empty, 5)).toEqual({ dx: 0, dy: 0, kind: null })
  })

  it('does nothing when disabled', () => {
    expect(snapMove(square(103, 98, 100), empty, 5, true)).toEqual({ dx: 0, dy: 0, kind: null })
  })
})

describe('useSnapping', () => {
  it('turns the 12 px threshold into centimetres at the current scale', () => {
    const snapping = useSnapping()
    expect(snapping.thresholdPx).toBe(12)
    // At 0.5 px per cm, 12 px is 24 cm: a point 20 cm from a grid line snaps to it.
    expect(snapping.snap(P(120, 90), empty, 0.5).point).toEqual(P(100, 100))
    // At 2 px per cm it is 6 cm: it does not.
    expect(snapping.snap(P(120, 90), empty, 2).kind).toBeNull()
  })
})
