import { distance, segmentsIntersect, type Point } from '../../../../components/floorplan/geometry'

/**
 * The Draw room tool: click to place corners, click the first corner (or press Enter) to close the outline, Esc to
 * cancel, Backspace to take back the last corner. Dragging on the empty plan draws a rectangle in one go.
 */
export interface DrawState {
  /** Corners placed so far, in order. */
  points: Point[]
}

export const emptyDraw = (): DrawState => ({ points: [] })

/** Corners closer than this (cm) are the same corner (a double click, a click on a placed corner). */
const SAME_POINT_CM = 0.5

export function canCloseDraw(state: DrawState): boolean {
  const distinct = state.points.filter(
    (point, index) => state.points.findIndex((other) => distance(other, point) < SAME_POINT_CM) === index,
  )
  return distinct.length >= 3
}

/**
 * A click at `point` (already snapped). Near the first corner (within `closeTolerance` cm) with three corners placed it
 * closes the outline (`closed` is the polygon and drawing starts over); near any placed corner it does nothing; anywhere
 * else it adds a corner.
 */
export function addDrawPoint(
  state: DrawState,
  point: Point,
  closeTolerance: number,
): { state: DrawState; closed: Point[] | null } {
  const first = state.points[0]
  if (first && canCloseDraw(state) && distance(first, point) <= closeTolerance) {
    return { state: emptyDraw(), closed: [...state.points] }
  }
  // On top of a placed corner (a double click, or the first corner before there are three): nothing to add.
  const duplicate = Math.min(closeTolerance, SAME_POINT_CM * 4)
  if (state.points.some((placed) => distance(placed, point) <= duplicate)) return { state, closed: null }
  if (first && distance(first, point) <= closeTolerance) return { state, closed: null }
  return { state: { points: [...state.points, { x: point.x, y: point.y }] }, closed: null }
}

/** Takes back the last corner. */
export function removeLastDrawPoint(state: DrawState): DrawState {
  return { points: state.points.slice(0, -1) }
}

/**
 * Whether the next wall, from the last corner to `point`, would cross a wall drawn so far (the previous wall shares the
 * last corner, so it is not counted). Shown as a warning on the rubber band.
 */
export function drawSegmentCrosses(points: readonly Point[], point: Point): boolean {
  const last = points.at(-1)
  if (!last) return false
  for (let i = 0; i < points.length - 2; i++) {
    if (segmentsIntersect(points[i]!, points[i + 1]!, last, point)) return true
  }
  return false
}

/** The four corners of the rectangle spanned by two opposite corners, clockwise from the top-left one. */
export function rectanglePolygon(a: Point, b: Point): Point[] {
  const minX = Math.min(a.x, b.x)
  const maxX = Math.max(a.x, b.x)
  const minY = Math.min(a.y, b.y)
  const maxY = Math.max(a.y, b.y)
  return [
    { x: minX, y: minY },
    { x: maxX, y: minY },
    { x: maxX, y: maxY },
    { x: minX, y: maxY },
  ]
}
