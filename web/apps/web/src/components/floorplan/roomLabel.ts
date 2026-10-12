/**
 * The label in the middle of a room: icon, name and area, sized to the room as it appears on screen. Names grow from
 * 13 px (small rooms) to 16 px (rooms that fill a desktop plan); parts drop out as the room gets small. Previews
 * (`compact`) show small names and, where there is room, the icon, never the area.
 */

export interface RoomLabelInput {
  name: string
  /** The room's bounding box on screen (px). */
  widthPx: number
  heightPx: number
  compact?: boolean
  /** Icons on (default). */
  showIcon?: boolean
}

export type RoomLabelLine = { kind: 'icon' | 'name' | 'area'; size: number; top: number; center: number }

export interface RoomLabelLayout {
  /** The name, shortened with an ellipsis when it does not fit. */
  name: string
  nameSize: number
  areaSize: number
  iconSize: number
  /** Lines from top to bottom, positioned around 0 (the label point). */
  lines: RoomLabelLine[]
}

/** Average glyph width (em): Fraunces names, Inter areas. */
const NAME_EM = 0.55
const AREA_EM = 0.6

/**
 * The screen space a label takes around its point (px): half its width (its widest line) and its top and bottom edges,
 * so other marks (the editor's wall lengths) can keep clear of it.
 */
export function roomLabelExtent(
  layout: RoomLabelLayout,
  areaText: string,
): { halfWidth: number; top: number; bottom: number } {
  const widths = layout.lines.map((line) =>
    line.kind === 'icon'
      ? layout.iconSize
      : line.kind === 'name'
        ? layout.name.length * layout.nameSize * NAME_EM
        : areaText.length * layout.areaSize * AREA_EM,
  )
  const last = layout.lines.at(-1)!
  return {
    halfWidth: Math.max(...widths) / 2,
    top: layout.lines[0]!.top,
    bottom: last.top + last.size,
  }
}

const halfStep = (value: number) => Math.round(value * 2) / 2
const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

export function roomLabelLayout(input: RoomLabelInput): RoomLabelLayout | null {
  const { widthPx, heightPx, compact = false, showIcon = true } = input
  if (widthPx < 36 || heightPx < 18) return null
  const shortSide = Math.min(widthPx, heightPx)
  const nameSize = compact ? 11 : halfStep(clamp(13 + (shortSide - 120) / 60, 13, 16))
  const areaSize = Math.max(11, halfStep(nameSize * 0.82))
  const iconSize = compact ? 14 : nameSize >= 15 ? 18 : 16

  const room = input.name.trim()
  const fitChars = Math.max(1, Math.floor((widthPx * 0.86) / (nameSize * NAME_EM)))
  const name = room.length > fitChars ? `${room.slice(0, Math.max(1, fitChars - 1)).trimEnd()}…` : room

  const withArea = !compact && heightPx >= 40 && widthPx >= 44
  const withIcon = showIcon && (compact ? heightPx >= 64 && widthPx >= 64 : heightPx >= 76 && widthPx >= 44)

  const gap = 3
  const sized = [
    ...(withIcon ? [{ kind: 'icon' as const, size: iconSize }] : []),
    { kind: 'name' as const, size: nameSize * 1.25 },
    ...(withArea ? [{ kind: 'area' as const, size: areaSize * 1.3 }] : []),
  ]
  const total = sized.reduce((sum, line) => sum + line.size, 0) + gap * (sized.length - 1)
  let y = -total / 2
  const lines = sized.map((line) => {
    const top = y
    y += line.size + gap
    return { ...line, top, center: top + line.size / 2 }
  })
  return { name, nameSize, areaSize, iconSize, lines }
}
