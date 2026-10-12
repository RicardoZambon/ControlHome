import { contains, distance, type Point } from '../../../components/floorplan/geometry'

/**
 * Where the selected room's wall lengths go: a small pill per wall, just outside the wall and clear of its midpoint
 * handle. When the outside is taken (a neighbouring room, or the edge of the floor, where the canvas ends), the pill
 * moves to the room's own side of the wall. A pill that would cover one already placed, or a room's own label, is
 * left out (the panel always shows the room's width × depth).
 */

export interface WallLabelOptions {
  /** The other rooms on the floor. */
  others: readonly (readonly Point[])[]
  widthCm: number
  heightCm: number
  /** Screen px per cm. */
  scale: number
  /** Walls shorter than this on screen get no label. */
  minWallPx: number
  /** Screen px between the wall and the pill (room for the midpoint handle). */
  gapPx: number
  /** The text for a wall length. */
  text: (cm: number) => string
  /** Areas a pill must not cover (cm): the rooms' own labels. */
  avoid?: readonly Box[]
}

export interface WallLabel {
  /** The wall: from corner `index` to the next one. */
  index: number
  cm: number
  text: string
  /** The pill's centre (cm). */
  at: Point
  /** Screen size of the pill. */
  widthPx: number
  heightPx: number
  /** On the room's own side of the wall. */
  inside: boolean
}

/** Pill height (px) and the width of its text: about 0.56 em per character at 11.5 px, plus padding. */
export const WALL_LABEL_HEIGHT_PX = 18
export const wallLabelWidthPx = (text: string) => Math.round(text.length * 6.5 + 14)

/** An axis-aligned box (cm). */
export interface Box {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

const overlaps = (a: Box, b: Box) => a.minX < b.maxX && b.minX < a.maxX && a.minY < b.maxY && b.minY < a.maxY

export function wallLabels(polygon: readonly Point[], options: WallLabelOptions): WallLabel[] {
  const { scale } = options
  const placed: Box[] = [...(options.avoid ?? [])]
  const labels: WallLabel[] = []
  if (polygon.length < 3) return labels

  for (let index = 0; index < polygon.length; index++) {
    const a = polygon[index]!
    const b = polygon[(index + 1) % polygon.length]!
    const cm = distance(a, b)
    if (cm * scale < options.minWallPx) continue

    const text = options.text(cm)
    const widthPx = wallLabelWidthPx(text)
    const heightPx = WALL_LABEL_HEIGHT_PX
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }
    // The wall's normal, pointing away from the room.
    let normal = { x: (b.y - a.y) / cm, y: -(b.x - a.x) / cm }
    const probe = 2 / scale
    if (contains(polygon, { x: mid.x + normal.x * probe, y: mid.y + normal.y * probe })) {
      normal = { x: -normal.x, y: -normal.y }
    }
    // Far enough along the normal that the whole pill clears the wall by the gap.
    const offsetPx = options.gapPx + Math.abs(normal.x) * (widthPx / 2) + Math.abs(normal.y) * (heightPx / 2)
    const at = (side: 1 | -1): Point => ({
      x: mid.x + (side * normal.x * offsetPx) / scale,
      y: mid.y + (side * normal.y * offsetPx) / scale,
    })
    const boxOf = (centre: Point): Box => ({
      minX: centre.x - widthPx / 2 / scale,
      maxX: centre.x + widthPx / 2 / scale,
      minY: centre.y - heightPx / 2 / scale,
      maxY: centre.y + heightPx / 2 / scale,
    })
    const offFloor = (box: Box) =>
      box.minX < 0 || box.minY < 0 || box.maxX > options.widthCm || box.maxY > options.heightCm
    const taken = (centre: Point) => options.others.some((other) => contains(other, centre))

    const outside = at(1)
    const inside = offFloor(boxOf(outside)) || taken(outside)
    const centre = inside ? at(-1) : outside
    const box = boxOf(centre)
    if (placed.some((other) => overlaps(other, box))) continue
    placed.push(box)
    labels.push({ index, cm, text, at: centre, widthPx, heightPx, inside })
  }
  return labels
}
