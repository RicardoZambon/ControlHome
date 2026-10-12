/**
 * Plane geometry for floor plans, mirroring `ControlHome.Domain/Geometry/PolygonMath.cs` and
 * `ControlHome.Application/Floors/FloorValidators.cs` (`RoomPolygonRules`) rule for rule, with the same tests
 * (`geometry.test.ts`), so the editor flags exactly what the server would reject.
 *
 * Units are centimetres; the origin is the floor's top-left corner and y grows downwards (SVG coordinates). A polygon is
 * implicitly closed (do not repeat the first point) and may wind either way.
 */

export interface Point {
  x: number
  y: number
}

export interface Bounds {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

/** Mirrors `FloorRules.MaxPolygonPoints`. */
export const MAX_POLYGON_POINTS = 500
/** Mirrors `FloorRules.MinRoomAreaCm2`: rooms smaller than this count as having no area. */
export const MIN_ROOM_AREA_CM2 = 1
/** Mirrors `FloorRules.NameMaxLength` (room and floor names). */
export const NAME_MAX_LENGTH = 80
/** Mirrors `FloorRules.MaxRooms`. */
export const MAX_ROOMS = 200

/** The `geometry.*` error codes of a room outline (`errors["rooms[i].polygon"]`). */
export type GeometryErrorCode =
  | 'geometry.too_few_points'
  | 'geometry.too_many_points'
  | 'geometry.self_intersecting'
  | 'geometry.out_of_bounds'
  | 'geometry.zero_area'

/** Relative tolerance: sin(angle) below this counts as collinear. */
const RELATIVE_EPSILON = 1e-9

const samePoint = (a: Point, b: Point) => a.x === b.x && a.y === b.y

function distinctCount(polygon: readonly Point[]): number {
  return new Set(polygon.map((p) => `${p.x}\u0000${p.y}`)).size
}

export function distance(a: Point, b: Point): number {
  return Math.hypot(b.x - a.x, b.y - a.y)
}

/** The z component of (b − a) × (c − a): positive, negative or zero by turn direction. */
function cross(a: Point, b: Point, c: Point): number {
  return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)
}

/** The turn a → b → c: 1, −1, or 0 when (within tolerance) collinear. */
function orientation(a: Point, b: Point, c: Point): number {
  const value = cross(a, b, c)
  const scale = distance(a, b) * distance(a, c)
  return Math.abs(value) <= RELATIVE_EPSILON * scale ? 0 : Math.sign(value)
}

/** For a point collinear with a–b: whether it is between them (tolerance relative to the segment length). */
function isWithinBox(a: Point, b: Point, point: Point): boolean {
  const tolerance = RELATIVE_EPSILON * (1 + distance(a, b))
  return (
    point.x >= Math.min(a.x, b.x) - tolerance &&
    point.x <= Math.max(a.x, b.x) + tolerance &&
    point.y >= Math.min(a.y, b.y) - tolerance &&
    point.y <= Math.max(a.y, b.y) + tolerance
  )
}

/** Whether `point` lies on the closed segment a–b. */
function isOnSegment(a: Point, b: Point, point: Point): boolean {
  return orientation(a, b, point) === 0 && isWithinBox(a, b, point)
}

/** Whether the closed segments p1–p2 and q1–q2 share any point (crossing, touching or overlapping). */
export function segmentsIntersect(p1: Point, p2: Point, q1: Point, q2: Point): boolean {
  const d1 = orientation(q1, q2, p1)
  const d2 = orientation(q1, q2, p2)
  const d3 = orientation(p1, p2, q1)
  const d4 = orientation(p1, p2, q2)
  if (d1 * d2 < 0 && d3 * d4 < 0) return true
  return (
    (d1 === 0 && isWithinBox(q1, q2, p1)) ||
    (d2 === 0 && isWithinBox(q1, q2, p2)) ||
    (d3 === 0 && isWithinBox(p1, p2, q1)) ||
    (d4 === 0 && isWithinBox(p1, p2, q2))
  )
}

/**
 * For consecutive edges previous → shared → next: whether the second runs back along the first (collinear, same
 * direction from the shared vertex), i.e. they overlap.
 */
function foldsBack(previous: Point, shared: Point, next: Point): boolean {
  if (orientation(shared, previous, next) !== 0) return false
  const dot = (previous.x - shared.x) * (next.x - shared.x) + (previous.y - shared.y) * (next.y - shared.y)
  return dot > 0
}

/** Twice the signed area (triangle fan from the first vertex). */
function signedDoubleArea(polygon: readonly Point[]): number {
  const origin = polygon[0]!
  let sum = 0
  for (let i = 1; i < polygon.length - 1; i++) sum += cross(origin, polygon[i]!, polygon[i + 1]!)
  return sum
}

export function boundsOf(polygon: readonly Point[]): Bounds {
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const p of polygon) {
    minX = Math.min(minX, p.x)
    minY = Math.min(minY, p.y)
    maxX = Math.max(maxX, p.x)
    maxY = Math.max(maxY, p.y)
  }
  return { minX, minY, maxX, maxY }
}

/** The enclosed area in cm² (absolute value of the shoelace formula); 0 for fewer than 3 points. */
export function areaCm2(polygon: readonly Point[]): number {
  return polygon.length < 3 ? 0 : Math.abs(signedDoubleArea(polygon)) / 2
}

/**
 * True when the polygon is simple: at least 3 distinct points, no repeated consecutive vertex, and no two edges touch
 * except consecutive edges at their shared vertex (no crossing, no pinch, no fold-back, not all points on one line).
 */
export function isSimple(polygon: readonly Point[]): boolean {
  const count = polygon.length
  if (distinctCount(polygon) < 3) return false
  for (let i = 0; i < count; i++) {
    if (samePoint(polygon[i]!, polygon[(i + 1) % count]!)) return false
  }
  for (let i = 0; i < count; i++) {
    const a1 = polygon[i]!
    const a2 = polygon[(i + 1) % count]!
    for (let j = i + 1; j < count; j++) {
      const b1 = polygon[j]!
      const b2 = polygon[(j + 1) % count]!
      const touching =
        j === i + 1
          ? foldsBack(a1, a2, b2) // edges a1→a2 and a2→b2 share a2
          : i === 0 && j === count - 1
            ? foldsBack(b1, a1, a2) // edges b1→a1 and a1→a2 share a1
            : segmentsIntersect(a1, a2, b1, b2)
      if (touching) return false
    }
  }
  return true
}

/**
 * True when `point` is inside the polygon or on its boundary (ray casting, with an explicit boundary test first).
 * Always false for fewer than 3 points.
 */
export function contains(polygon: readonly Point[], point: Point): boolean {
  const count = polygon.length
  if (count < 3) return false
  for (let i = 0; i < count; i++) {
    if (isOnSegment(polygon[i]!, polygon[(i + 1) % count]!, point)) return true
  }
  // Half-open rule (an edge counts when exactly one endpoint is above the ray), so a ray through a vertex counts once.
  let inside = false
  for (let i = 0, j = count - 1; i < count; j = i++) {
    const current = polygon[i]!
    const previous = polygon[j]!
    if (current.y > point.y !== previous.y > point.y) {
      const crossingX =
        current.x + ((previous.x - current.x) * (point.y - current.y)) / (previous.y - current.y)
      if (point.x < crossingX) inside = !inside
    }
  }
  return inside
}

/**
 * The area-weighted centroid. For points enclosing no area (fewer than 3, or all on a line) it is the average of the
 * points. Throws for no points.
 */
export function centroid(polygon: readonly Point[]): Point {
  if (polygon.length === 0) throw new Error('A centroid needs at least one point.')
  const doubleArea = polygon.length < 3 ? 0 : signedDoubleArea(polygon)
  const { minX, minY, maxX, maxY } = boundsOf(polygon)
  if (Math.abs(doubleArea) <= RELATIVE_EPSILON * (maxX - minX) * (maxY - minY)) {
    return {
      x: polygon.reduce((sum, p) => sum + p.x, 0) / polygon.length,
      y: polygon.reduce((sum, p) => sum + p.y, 0) / polygon.length,
    }
  }
  // Relative to the first vertex, for precision with large coordinates.
  const origin = polygon[0]!
  let sumX = 0
  let sumY = 0
  for (let i = 0; i < polygon.length; i++) {
    const x1 = polygon[i]!.x - origin.x
    const y1 = polygon[i]!.y - origin.y
    const next = polygon[(i + 1) % polygon.length]!
    const x2 = next.x - origin.x
    const y2 = next.y - origin.y
    const c = x1 * y2 - x2 * y1
    sumX += (x1 + x2) * c
    sumY += (y1 + y2) * c
  }
  return { x: origin.x + sumX / (3 * doubleArea), y: origin.y + sumY / (3 * doubleArea) }
}

/**
 * True when the two polygons share at least one point: their edges cross or touch, or one lies inside the other.
 * Always false when either has fewer than 3 points.
 */
export function intersects(a: readonly Point[], b: readonly Point[]): boolean {
  if (a.length < 3 || b.length < 3) return false
  for (let i = 0; i < a.length; i++) {
    const a1 = a[i]!
    const a2 = a[(i + 1) % a.length]!
    for (let j = 0; j < b.length; j++) {
      if (segmentsIntersect(a1, a2, b[j]!, b[(j + 1) % b.length]!)) return true
    }
  }
  return contains(a, b[0]!) || contains(b, a[0]!)
}

/** Whether the point lies on the floor (edges included). NaN and infinities never do. */
export function isWithinFloor(point: Point, widthCm: number, heightCm: number): boolean {
  return point.x >= 0 && point.x <= widthCm && point.y >= 0 && point.y <= heightCm
}

/** Whether every point lies (within a micrometre) on the line through the first two distinct points. */
function allCollinear(polygon: readonly Point[]): boolean {
  const toleranceCm = 1e-4
  const distinct = polygon.filter((p, index) => polygon.findIndex((other) => samePoint(other, p)) === index)
  const first = distinct[0]!
  const second = distinct[1]!
  const dx = second.x - first.x
  const dy = second.y - first.y
  const length = Math.hypot(dx, dy)
  return distinct.every((p) => Math.abs(dx * (p.y - first.y) - dy * (p.x - first.x)) / length <= toleranceCm)
}

/**
 * The first rule a room outline breaks on a floor of `widthCm × heightCm`, as the server's error code, or `null` when it
 * is valid (mirrors `RoomPolygonRules.Validate`, in the same order).
 */
export function validateRoomPolygon(
  polygon: readonly Point[],
  widthCm: number,
  heightCm: number,
): GeometryErrorCode | null {
  if (polygon.length > MAX_POLYGON_POINTS) return 'geometry.too_many_points'
  if (distinctCount(polygon) < 3) return 'geometry.too_few_points'
  if (!polygon.every((p) => isWithinFloor(p, widthCm, heightCm))) return 'geometry.out_of_bounds'
  const area = areaCm2(polygon)
  if (!isSimple(polygon)) {
    // Points on one line enclose nothing (and their edges overlap): report that, not a crossing.
    return area < MIN_ROOM_AREA_CM2 && allCollinear(polygon)
      ? 'geometry.zero_area'
      : 'geometry.self_intersecting'
  }
  return area < MIN_ROOM_AREA_CM2 ? 'geometry.zero_area' : null
}

/** The point of segment a–b closest to `point`. */
export function closestPointOnSegment(point: Point, a: Point, b: Point): Point {
  const dx = b.x - a.x
  const dy = b.y - a.y
  const lengthSquared = dx * dx + dy * dy
  if (lengthSquared === 0) return { x: a.x, y: a.y }
  const t = Math.max(0, Math.min(1, ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared))
  return { x: a.x + t * dx, y: a.y + t * dy }
}

/**
 * Where a room's label goes: the centroid when it lies inside the room, else (concave rooms such as a U) the middle of
 * the widest inside stretch of the horizontal line through the centroid.
 */
export function labelPoint(polygon: readonly Point[]): Point {
  const c = centroid(polygon)
  if (polygon.length < 3 || contains(polygon, c)) return c

  const { minY, maxY } = boundsOf(polygon)
  // Scan a few horizontal lines, starting at the centroid's, and keep the widest inside stretch.
  const lines = [c.y, ...[0.5, 0.25, 0.75, 0.375, 0.625].map((t) => minY + (maxY - minY) * t)]
  let best: { point: Point; width: number } | null = null
  for (const y of lines) {
    const xs: number[] = []
    for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
      const p = polygon[i]!
      const q = polygon[j]!
      if (p.y > y !== q.y > y) xs.push(p.x + ((q.x - p.x) * (y - p.y)) / (q.y - p.y))
    }
    xs.sort((a, b) => a - b)
    for (let k = 0; k + 1 < xs.length; k += 2) {
      const width = xs[k + 1]! - xs[k]!
      if (!best || width > best.width) best = { point: { x: (xs[k]! + xs[k + 1]!) / 2, y }, width }
    }
    if (best && y === c.y) break
  }
  return best?.point ?? c
}
