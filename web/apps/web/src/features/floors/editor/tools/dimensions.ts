import { boundsOf, type Point } from '../../../../components/floorplan/geometry'

/**
 * Typed sizes for rooms: most rooms are rectangles, and people know their width and depth in metres. These read a
 * room's size and rebuild a rectangle from typed numbers (the properties panel's Width × Depth and X / Y fields).
 */

export interface RectangleCm {
  /** Left edge (cm from the floor's left). */
  x: number
  /** Top edge (cm from the floor's top). */
  y: number
  width: number
  height: number
}

/** Below this (cm) two coordinates are the same line. */
const EPSILON = 0.001

/**
 * The room as an axis-aligned rectangle: four corners, walls alternating horizontal and vertical, none of zero length.
 * `null` for any other shape (an L-shape, a slanted wall, a rotated square).
 */
export function rectangleOf(polygon: readonly Point[]): RectangleCm | null {
  if (polygon.length !== 4) return null
  let previous: 'h' | 'v' | null = null
  for (let index = 0; index < 4; index++) {
    const a = polygon[index]!
    const b = polygon[(index + 1) % 4]!
    const horizontal = Math.abs(a.y - b.y) < EPSILON
    const vertical = Math.abs(a.x - b.x) < EPSILON
    if (horizontal === vertical) return null
    const direction = horizontal ? 'h' : 'v'
    if (direction === previous) return null
    previous = direction
  }
  const { minX, minY, maxX, maxY } = boundsOf(polygon)
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY }
}

/** The width and depth of the box around the room (its size for any shape). */
export function sizeOf(polygon: readonly Point[]): { width: number; height: number } {
  const { minX, minY, maxX, maxY } = boundsOf(polygon)
  return { width: maxX - minX, height: maxY - minY }
}

/**
 * A rectangular room moved and resized to `target`, each corner keeping its place in the outline (so the room's
 * corner order, and any selected corner, stay the same). `null` when the room is not a rectangle.
 */
export function resizeRectangle(polygon: readonly Point[], target: RectangleCm): Point[] | null {
  const current = rectangleOf(polygon)
  if (!current) return null
  const midX = current.x + current.width / 2
  const midY = current.y + current.height / 2
  return polygon.map((corner) => ({
    x: corner.x < midX ? target.x : target.x + target.width,
    y: corner.y < midY ? target.y : target.y + target.height,
  }))
}
