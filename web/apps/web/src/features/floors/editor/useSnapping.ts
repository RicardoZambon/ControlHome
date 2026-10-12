import { closestPointOnSegment, distance, type Point } from '../../../components/floorplan/geometry'

export type SnapKind = 'vertex' | 'edge' | 'grid'

/** What a point can snap to: the editor grid, vertices and edges (other rooms' outlines, own placed points). */
export interface SnapContext {
  gridCm: number
  vertices: readonly Point[]
  edges: readonly (readonly [Point, Point])[]
}

export interface SnapResult {
  point: Point
  kind: SnapKind | null
}

interface Candidate {
  point: Point
  kind: SnapKind
  distance: number
}

/** On ties a vertex beats an edge beats the grid. */
const PRIORITY: Record<SnapKind, number> = { vertex: 0, edge: 1, grid: 2 }

const better = (a: Candidate, b: Candidate | null) =>
  !b || a.distance < b.distance || (a.distance === b.distance && PRIORITY[a.kind] < PRIORITY[b.kind])

/** The closest vertex or edge point within `threshold` cm (an edge point near an end is that end's vertex). */
function featureCandidate(point: Point, context: SnapContext, threshold: number): Candidate | null {
  let best: Candidate | null = null
  for (const vertex of context.vertices) {
    const d = distance(point, vertex)
    const candidate: Candidate = { point: { x: vertex.x, y: vertex.y }, kind: 'vertex', distance: d }
    if (d <= threshold && better(candidate, best)) best = candidate
  }
  for (const [a, b] of context.edges) {
    const onEdge = closestPointOnSegment(point, a, b)
    const d = distance(point, onEdge)
    if (d > threshold || distance(onEdge, a) <= threshold || distance(onEdge, b) <= threshold) continue
    const candidate: Candidate = { point: onEdge, kind: 'edge', distance: d }
    if (better(candidate, best)) best = candidate
  }
  return best
}

/** Each axis on its own to the nearest grid line within `threshold` cm. */
function gridCandidate(point: Point, gridCm: number, threshold: number): Candidate | null {
  if (!(gridCm > 0)) return null
  const gx = Math.round(point.x / gridCm) * gridCm
  const gy = Math.round(point.y / gridCm) * gridCm
  const snapsX = Math.abs(gx - point.x) <= threshold
  const snapsY = Math.abs(gy - point.y) <= threshold
  if (!snapsX && !snapsY) return null
  const snapped = { x: snapsX ? gx : point.x, y: snapsY ? gy : point.y }
  return { point: snapped, kind: 'grid', distance: distance(point, snapped) }
}

/**
 * Snaps `point` to the closest of: a vertex, a point on an edge, the grid (each axis separately), all within
 * `threshold` cm. `disabled` (Alt held) returns the point unchanged.
 */
export function snapPoint(
  point: Point,
  context: SnapContext,
  threshold: number,
  disabled = false,
): SnapResult {
  if (disabled) return { point, kind: null }
  let best = featureCandidate(point, context, threshold)
  const grid = gridCandidate(point, context.gridCm, threshold)
  if (grid && better(grid, best)) best = grid
  return best ? { point: best.point, kind: best.kind } : { point, kind: null }
}

/**
 * For a room being dragged (`polygon` already at its moved position): the shift that puts its vertex closest to another
 * room's vertex or wall onto it; failing that, the shift that puts its first vertex on the grid.
 */
export function snapMove(
  polygon: readonly Point[],
  context: SnapContext,
  threshold: number,
  disabled = false,
): { dx: number; dy: number; kind: SnapKind | null } {
  if (disabled || polygon.length === 0) return { dx: 0, dy: 0, kind: null }
  let best: { dx: number; dy: number; kind: SnapKind; distance: number } | null = null
  for (const vertex of polygon) {
    const candidate = featureCandidate(vertex, context, threshold)
    if (candidate && (!best || candidate.distance < best.distance)) {
      best = {
        dx: candidate.point.x - vertex.x,
        dy: candidate.point.y - vertex.y,
        kind: candidate.kind,
        distance: candidate.distance,
      }
    }
  }
  if (best) return { dx: best.dx, dy: best.dy, kind: best.kind }
  const anchor = polygon[0]!
  const grid = gridCandidate(anchor, context.gridCm, threshold)
  return grid
    ? { dx: grid.point.x - anchor.x, dy: grid.point.y - anchor.y, kind: 'grid' }
    : { dx: 0, dy: 0, kind: null }
}

/**
 * The snap targets for editing: every vertex and edge of the rooms except `excludeKey` (the room being edited or moved
 * does not snap to its own outline), plus `ownVertices` (e.g. the points already placed while drawing).
 */
export function snapContext(
  rooms: readonly { key: string; polygon: readonly Point[] }[],
  options: { gridCm: number; excludeKey?: string | null; ownVertices?: readonly Point[] },
): SnapContext {
  const vertices: Point[] = []
  const edges: [Point, Point][] = []
  for (const room of rooms) {
    if (room.key === options.excludeKey) continue
    vertices.push(...room.polygon)
    room.polygon.forEach((point, index) => {
      edges.push([point, room.polygon[(index + 1) % room.polygon.length]!])
    })
  }
  vertices.push(...(options.ownVertices ?? []))
  return { gridCm: options.gridCm, vertices, edges }
}

/**
 * Snapping with a threshold in screen pixels (default 12), converted to centimetres at the canvas's current scale
 * (px per cm), so snapping feels the same at every zoom level.
 */
export function useSnapping(thresholdPx = 12) {
  const toCm = (scale: number) => thresholdPx / Math.max(scale, 1e-6)
  return {
    thresholdPx,
    thresholdCm: toCm,
    snap: (point: Point, context: SnapContext, scale: number, disabled = false) =>
      snapPoint(point, context, toCm(scale), disabled),
    snapMove: (polygon: readonly Point[], context: SnapContext, scale: number, disabled = false) =>
      snapMove(polygon, context, toCm(scale), disabled),
  }
}
