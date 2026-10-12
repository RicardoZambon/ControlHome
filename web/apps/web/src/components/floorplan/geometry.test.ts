import { describe, expect, it } from 'vitest'
import {
  MAX_POLYGON_POINTS,
  areaCm2,
  boundsOf,
  centroid,
  closestPointOnSegment,
  contains,
  distance,
  intersects,
  isSimple,
  labelPoint,
  validateRoomPolygon,
  type Point,
} from './geometry'

// Ported one to one from tests/ControlHome.Domain.Tests/Geometry/PolygonMathTests.cs (PolygonMath) and the geometry
// cases of FloorLayoutTests (RoomPolygonRules), so the editor and the server agree on every rule.

const P = (x: number, y: number): Point => ({ x, y })

/** A 100 × 100 cm square at the origin (clockwise in screen coordinates: y grows downwards). */
const square: Point[] = [P(0, 0), P(100, 0), P(100, 100), P(0, 100)]

/** An L: the left column (0..100 × 0..200) plus the bottom-right block (100..200 × 100..200). Area 30 000 cm². */
const lShape: Point[] = [P(0, 0), P(100, 0), P(100, 100), P(200, 100), P(200, 200), P(0, 200)]

/** A U opening upwards: two 100 cm arms and a 100 cm base, the notch is 100..200 × 0..200. */
const uShape: Point[] = [
  P(0, 0),
  P(100, 0),
  P(100, 200),
  P(200, 200),
  P(200, 0),
  P(300, 0),
  P(300, 300),
  P(0, 300),
]

/** Two triangles meeting in the middle: edges 0 and 2 cross at (50, 50). */
const bowTie: Point[] = [P(0, 0), P(100, 100), P(100, 0), P(0, 100)]

const Square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

const reversed = (polygon: Point[]) => [...polygon].reverse()

describe('areaCm2', () => {
  it('of a square', () => {
    expect(areaCm2(square)).toBeCloseTo(10_000, 9)
  })

  it('does not depend on winding', () => {
    expect(areaCm2(reversed(square))).toBeCloseTo(10_000, 9)
  })

  it('of a concave polygon', () => {
    expect(areaCm2(lShape)).toBeCloseTo(30_000, 9)
    expect(areaCm2(uShape)).toBeCloseTo(70_000, 9)
  })

  it.each([0, 1, 2])('of fewer than three points (%i) is zero', (count) => {
    expect(areaCm2(square.slice(0, count))).toBe(0)
  })
})

describe('isSimple', () => {
  it('square, triangle and concave polygons are simple', () => {
    expect(isSimple(square)).toBe(true)
    expect(isSimple([P(0, 0), P(100, 0), P(50, 80)])).toBe(true)
    expect(isSimple(lShape)).toBe(true)
    expect(isSimple(uShape)).toBe(true)
    expect(isSimple(reversed(square))).toBe(true)
  })

  it('bow tie is not simple', () => {
    expect(isSimple(bowTie)).toBe(false)
  })

  it('fewer than three distinct points is not simple', () => {
    expect(isSimple([])).toBe(false)
    expect(isSimple([P(0, 0), P(100, 0)])).toBe(false)
    expect(isSimple([P(0, 0), P(100, 0), P(0, 0)])).toBe(false)
    expect(isSimple([P(5, 5), P(5, 5), P(5, 5), P(5, 5)])).toBe(false)
  })

  it('collinear points are not simple', () => {
    expect(isSimple([P(0, 0), P(100, 0), P(200, 0)])).toBe(false)
    expect(isSimple([P(0, 0), P(50, 50), P(100, 100), P(25, 25)])).toBe(false)
  })

  it('a repeated vertex is not simple', () => {
    expect(isSimple([P(0, 0), P(100, 0), P(100, 0), P(100, 100), P(0, 100)])).toBe(false)
  })

  it('a closing point equal to the first is not simple', () => {
    expect(isSimple([...square, P(0, 0)])).toBe(false)
  })

  it('a vertex touching another edge is not simple', () => {
    // The vertex (50, 0) lies on the edge (0, 0)-(100, 0): the polygon pinches itself there.
    expect(isSimple([P(0, 0), P(100, 0), P(100, 100), P(50, 0), P(0, 100)])).toBe(false)
  })

  it('an edge folding back over the previous one is not simple', () => {
    expect(isSimple([P(0, 0), P(200, 0), P(100, 0), P(100, 100)])).toBe(false)
  })

  it('two squares joined at a corner are not simple', () => {
    expect(
      isSimple([
        P(0, 0),
        P(100, 0),
        P(100, 100),
        P(200, 100),
        P(200, 200),
        P(100, 200),
        P(100, 100),
        P(0, 100),
      ]),
    ).toBe(false)
  })
})

describe('contains', () => {
  it('point inside and outside a square', () => {
    expect(contains(square, P(50, 50))).toBe(true)
    expect(contains(square, P(150, 50))).toBe(false)
    expect(contains(square, P(-0.001, 50))).toBe(false)
    expect(contains(square, P(50, 100.001))).toBe(false)
  })

  it.each([
    [50, 0],
    [100, 50],
    [50, 100],
    [0, 50],
    [0, 0],
    [100, 100],
  ])('a point on the boundary (%d, %d) counts as inside', (x, y) => {
    expect(contains(square, P(x, y))).toBe(true)
  })

  it('a point on a diagonal edge counts as inside', () => {
    const triangle = [P(0, 0), P(300, 0), P(0, 300)]
    expect(contains(triangle, P(100, 200))).toBe(true)
    expect(contains(triangle, P(100.1, 200))).toBe(false)
  })

  it('concave polygon containment', () => {
    expect(contains(uShape, P(50, 100))).toBe(true) // left arm
    expect(contains(uShape, P(250, 100))).toBe(true) // right arm
    expect(contains(uShape, P(150, 250))).toBe(true) // base
    expect(contains(uShape, P(150, 100))).toBe(false) // the notch
    expect(contains(uShape, P(150, 200))).toBe(true) // the notch's floor is boundary

    expect(contains(lShape, P(150, 50))).toBe(false) // the missing corner
    expect(contains(lShape, P(150, 150))).toBe(true)
  })

  it('a ray through a vertex is counted once', () => {
    // A diamond: the horizontal ray from (10, 50) passes exactly through the vertices (0, 50) and (100, 50).
    const diamond = [P(50, 0), P(100, 50), P(50, 100), P(0, 50)]
    expect(contains(diamond, P(10, 50))).toBe(true)
    expect(contains(diamond, P(-10, 50))).toBe(false)
    expect(contains(diamond, P(110, 50))).toBe(false)
  })

  it('nothing is inside fewer than three points', () => {
    expect(contains([P(0, 0), P(100, 0)], P(50, 0))).toBe(false)
  })
})

describe('centroid', () => {
  it('of a square', () => {
    const c = centroid(Square(100, 200, 50))
    expect(c.x).toBeCloseTo(125, 9)
    expect(c.y).toBeCloseTo(225, 9)
  })

  it('of an L shape is area weighted', () => {
    // Column (area 20 000, centre (50, 100)) + block (area 10 000, centre (150, 150)).
    const c = centroid(lShape)
    expect(c.x).toBeCloseTo(250 / 3, 9)
    expect(c.y).toBeCloseTo(350 / 3, 9)
  })

  it('does not depend on winding', () => {
    const c = centroid(reversed(lShape))
    expect(c.x).toBeCloseTo(250 / 3, 9)
    expect(c.y).toBeCloseTo(350 / 3, 9)
  })

  it('of degenerate points is their average', () => {
    expect(centroid([P(0, 0), P(100, 0), P(200, 0)])).toEqual(P(100, 0))
    expect(centroid([P(10, 20)])).toEqual(P(10, 20))
  })

  it('of no points throws', () => {
    expect(() => centroid([])).toThrow()
  })
})

describe('intersects', () => {
  it('overlapping squares intersect', () => {
    expect(intersects(Square(0, 0, 100), Square(50, 50, 100))).toBe(true)
  })

  it('disjoint squares do not intersect', () => {
    expect(intersects(Square(0, 0, 100), Square(200, 0, 100))).toBe(false)
    expect(intersects(Square(0, 0, 100), Square(100.5, 0, 100))).toBe(false)
  })

  it('nested polygons intersect both ways', () => {
    expect(intersects(Square(0, 0, 300), Square(100, 100, 50))).toBe(true)
    expect(intersects(Square(100, 100, 50), Square(0, 0, 300))).toBe(true)
  })

  it('squares sharing a wall intersect', () => {
    expect(intersects(Square(0, 0, 100), Square(100, 0, 100))).toBe(true)
  })

  it('crossing bars intersect without containing a vertex', () => {
    const horizontal = [P(0, 40), P(300, 40), P(300, 60), P(0, 60)]
    const vertical = [P(140, 0), P(160, 0), P(160, 300), P(140, 300)]
    expect(intersects(horizontal, vertical)).toBe(true)
  })

  it('a polygon in the notch of a concave one does not intersect', () => {
    expect(intersects(uShape, Square(120, 20, 50))).toBe(false)
  })
})

describe('validateRoomPolygon (RoomPolygonRules, floor 2000 × 1500 cm)', () => {
  const check = (polygon: Point[]) => validateRoomPolygon(polygon, 2000, 1500)

  it('accepts a valid room, also one on the floor edge', () => {
    expect(check(Square(0, 0, 400))).toBeNull()
    expect(validateRoomPolygon([P(0, 0), P(1000, 0), P(1000, 800), P(0, 800)], 1000, 800)).toBeNull()
  })

  it('reports the same codes as the server', () => {
    expect(check([P(500, 0), P(800, 300), P(800, 0), P(500, 300)])).toBe('geometry.self_intersecting')
    expect(check(Square(1900, 0, 300))).toBe('geometry.out_of_bounds')
    expect(check([P(0, 500), P(100, 500)])).toBe('geometry.too_few_points')
    expect(check([P(0, 600), P(100, 600), P(200, 600)])).toBe('geometry.zero_area')
    expect(check([P(0, 700), P(100, 700), P(100, 700.005)])).toBe('geometry.zero_area')
    expect(check([P(-1, 800), P(100, 800), P(100, 900)])).toBe('geometry.out_of_bounds')
    expect(check([P(0, 1000), P(100, 1000), P(100, 1000), P(100, 1100)])).toBe('geometry.self_intersecting')
  })

  it('rejects more than the maximum number of points', () => {
    const count = MAX_POLYGON_POINTS + 1
    const circle = Array.from({ length: count }, (_, i) =>
      P(700 + 500 * Math.cos((i * 2 * Math.PI) / count), 700 + 500 * Math.sin((i * 2 * Math.PI) / count)),
    )
    expect(check(circle)).toBe('geometry.too_many_points')
  })

  it('never accepts NaN or infinite coordinates', () => {
    expect(check([P(0, 0), P(Number.NaN, 0), P(100, 100)])).toBe('geometry.out_of_bounds')
    expect(check([P(0, 0), P(Infinity, 0), P(100, 100)])).toBe('geometry.out_of_bounds')
  })
})

describe('helpers', () => {
  it('distance', () => {
    expect(distance(P(0, 0), P(30, 40))).toBe(50)
  })

  it('closestPointOnSegment clamps to the segment', () => {
    expect(closestPointOnSegment(P(50, 20), P(0, 0), P(100, 0))).toEqual(P(50, 0))
    expect(closestPointOnSegment(P(-20, 20), P(0, 0), P(100, 0))).toEqual(P(0, 0))
    expect(closestPointOnSegment(P(150, -5), P(0, 0), P(100, 0))).toEqual(P(100, 0))
    expect(closestPointOnSegment(P(5, 5), P(10, 10), P(10, 10))).toEqual(P(10, 10))
  })

  it('boundsOf', () => {
    expect(boundsOf(lShape)).toEqual({ minX: 0, minY: 0, maxX: 200, maxY: 200 })
  })

  it('labelPoint is the centroid when it lies inside the room', () => {
    expect(labelPoint(Square(0, 0, 100))).toEqual(P(50, 50))
    const c = labelPoint(lShape)
    expect(contains(lShape, c)).toBe(true)
  })

  it('labelPoint stays inside a room whose centroid is outside (the notch of a U)', () => {
    // The U's centroid is (150, ~164.3), inside the notch.
    expect(contains(uShape, centroid(uShape))).toBe(false)
    const label = labelPoint(uShape)
    expect(contains(uShape, label)).toBe(true)
  })
})
