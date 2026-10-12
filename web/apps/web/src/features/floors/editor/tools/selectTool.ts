import {
  boundsOf,
  closestPointOnSegment,
  contains,
  distance,
  type Point,
} from '../../../../components/floorplan/geometry'

/**
 * The Select tool: click a room to select it, drag it to move it, drag a corner handle to reshape it, double-click a
 * wall to add a corner there, Delete removes the selected corner (or the room). Pure geometry and hit testing here;
 * the pointer wiring lives in the editor canvas.
 */

export interface HitRoom {
  key: string
  polygon: readonly Point[]
}

export type Hit =
  | { kind: 'vertex'; roomKey: string; index: number }
  | { kind: 'edge'; roomKey: string; index: number; point: Point }
  | { kind: 'room'; roomKey: string }
  | { kind: 'none' }

interface EdgeHit {
  index: number
  point: Point
  distance: number
}

function nearestEdge(polygon: readonly Point[], point: Point): EdgeHit | null {
  let best: EdgeHit | null = null
  for (let index = 0; index < polygon.length; index++) {
    const onEdge = closestPointOnSegment(point, polygon[index]!, polygon[(index + 1) % polygon.length]!)
    const d = distance(point, onEdge)
    if (!best || d < best.distance) best = { index, point: onEdge, distance: d }
  }
  return best
}

/**
 * What is under `point` (cm), `tolerance` cm around handles and walls: a corner or a wall of the selected room, else the
 * selected room itself, else the topmost room containing the point (or with a wall within the tolerance).
 */
export function hitTest(
  rooms: readonly HitRoom[],
  point: Point,
  tolerance: number,
  selectedKey: string | null,
): Hit {
  const selected = selectedKey ? rooms.find((room) => room.key === selectedKey) : undefined
  if (selected) {
    let vertex = -1
    let vertexDistance = Infinity
    for (let index = 0; index < selected.polygon.length; index++) {
      const d = distance(selected.polygon[index]!, point)
      if (d <= tolerance && d < vertexDistance) {
        vertex = index
        vertexDistance = d
      }
    }
    if (vertex >= 0) return { kind: 'vertex', roomKey: selected.key, index: vertex }
    const edge = nearestEdge(selected.polygon, point)
    if (edge && edge.distance <= tolerance) {
      return { kind: 'edge', roomKey: selected.key, index: edge.index, point: edge.point }
    }
    if (contains(selected.polygon, point)) return { kind: 'room', roomKey: selected.key }
  }

  for (let i = rooms.length - 1; i >= 0; i--) {
    if (contains(rooms[i]!.polygon, point)) return { kind: 'room', roomKey: rooms[i]!.key }
  }
  for (let i = rooms.length - 1; i >= 0; i--) {
    const edge = nearestEdge(rooms[i]!.polygon, point)
    if (edge && edge.distance <= tolerance) return { kind: 'room', roomKey: rooms[i]!.key }
  }
  return { kind: 'none' }
}

/** The wall whose midpoint handle is within `tolerance` cm of `point` (pressing it adds a corner there). */
export function midpointHit(
  polygon: readonly Point[],
  point: Point,
  tolerance: number,
): { index: number; point: Point } | null {
  let best: { index: number; point: Point; distance: number } | null = null
  for (let index = 0; index < polygon.length; index++) {
    const a = polygon[index]!
    const b = polygon[(index + 1) % polygon.length]!
    const middle = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
    const d = distance(middle, point)
    if (d <= tolerance && (!best || d < best.distance)) best = { index, point: middle, distance: d }
  }
  return best ? { index: best.index, point: best.point } : null
}

export function moveVertex(polygon: readonly Point[], index: number, point: Point): Point[] {
  return polygon.map((corner, i) => (i === index ? { x: point.x, y: point.y } : corner))
}

/** Adds a corner on wall `edgeIndex` (between corners `edgeIndex` and `edgeIndex + 1`). */
export function insertVertex(polygon: readonly Point[], edgeIndex: number, point: Point): Point[] {
  return [...polygon.slice(0, edgeIndex + 1), { x: point.x, y: point.y }, ...polygon.slice(edgeIndex + 1)]
}

/** Removes a corner; `null` when only three are left (a room keeps at least three). */
export function removeVertex(polygon: readonly Point[], index: number): Point[] | null {
  if (polygon.length <= 3) return null
  return polygon.filter((_, i) => i !== index)
}

export function translatePolygon(polygon: readonly Point[], dx: number, dy: number): Point[] {
  return polygon.map((corner) => ({ x: corner.x + dx, y: corner.y + dy }))
}

/** Limits a move of the whole room so it stays on the `widthCm × heightCm` floor. */
export function clampMove(
  polygon: readonly Point[],
  dx: number,
  dy: number,
  widthCm: number,
  heightCm: number,
): { dx: number; dy: number } {
  const { minX, minY, maxX, maxY } = boundsOf(polygon)
  const clampAxis = (delta: number, min: number, max: number, size: number) =>
    max - min > size ? delta : Math.min(size - max, Math.max(-min, delta))
  return { dx: clampAxis(dx, minX, maxX, widthCm), dy: clampAxis(dy, minY, maxY, heightCm) }
}

export function clampToFloor(point: Point, widthCm: number, heightCm: number): Point {
  return { x: Math.min(widthCm, Math.max(0, point.x)), y: Math.min(heightCm, Math.max(0, point.y)) }
}

/** Screen pixels a pointer must move before a press becomes a drag (a click that wobbles stays a click). */
export const DRAG_THRESHOLD_PX = 4

export function exceedsDragThreshold(start: Point, current: Point): boolean {
  return distance(start, current) > DRAG_THRESHOLD_PX
}
