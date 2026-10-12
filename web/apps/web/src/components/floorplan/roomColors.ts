/**
 * Room colors are design tokens (`--room-1` … `--room-8` in tokens.css, light and dark), never raw colors. The classes
 * are spelled out in full so Tailwind's scanner generates them (a class built at runtime, `fill-${token}`, would not
 * exist).
 *
 * Fills are translucent so the grid (and, while editing, the traced image) shows through. Dark surfaces need a
 * stronger mix than light ones (25 % of a light hue over near-black is a muddy dark that hides the hue), so every fill
 * has its own dark-theme strength.
 */
export const ROOM_COLOR_TOKENS = [
  'room-1',
  'room-2',
  'room-3',
  'room-4',
  'room-5',
  'room-6',
  'room-7',
  'room-8',
] as const

export type RoomColorToken = (typeof ROOM_COLOR_TOKENS)[number]

export interface RoomColorClasses {
  /** Room fill at rest. */
  fill: string
  /** Room fill under the pointer (a small step up). */
  fillHover: string
  /** Room fill on previews (a little stronger, so rooms read at a glance). */
  fillStrong: string
  /** Outline in the solid token. */
  stroke: string
  /** A solid swatch (small dots). */
  swatch: string
  /**
   * The color as it looks on the plan: the room fill on a card, outlined in the solid token (color pickers, icon tiles).
   * Put it on a `bg-card` element.
   */
  sample: string
  /** A soft tint for list rows and chips. */
  tint: string
}

const CLASSES: Record<RoomColorToken, RoomColorClasses> = {
  'room-1': {
    fill: 'fill-room-1/25 dark:fill-room-1/40',
    fillHover: 'fill-room-1/32 dark:fill-room-1/50',
    fillStrong: 'fill-room-1/35 dark:fill-room-1/50',
    stroke: 'stroke-room-1',
    swatch: 'bg-room-1',
    sample: 'bg-room-1/25 dark:bg-room-1/40 border-room-1',
    tint: 'bg-room-1/15 dark:bg-room-1/25',
  },
  'room-2': {
    fill: 'fill-room-2/25 dark:fill-room-2/40',
    fillHover: 'fill-room-2/32 dark:fill-room-2/50',
    fillStrong: 'fill-room-2/35 dark:fill-room-2/50',
    stroke: 'stroke-room-2',
    swatch: 'bg-room-2',
    sample: 'bg-room-2/25 dark:bg-room-2/40 border-room-2',
    tint: 'bg-room-2/15 dark:bg-room-2/25',
  },
  'room-3': {
    fill: 'fill-room-3/25 dark:fill-room-3/40',
    fillHover: 'fill-room-3/32 dark:fill-room-3/50',
    fillStrong: 'fill-room-3/35 dark:fill-room-3/50',
    stroke: 'stroke-room-3',
    swatch: 'bg-room-3',
    sample: 'bg-room-3/25 dark:bg-room-3/40 border-room-3',
    tint: 'bg-room-3/15 dark:bg-room-3/25',
  },
  'room-4': {
    fill: 'fill-room-4/25 dark:fill-room-4/40',
    fillHover: 'fill-room-4/32 dark:fill-room-4/50',
    fillStrong: 'fill-room-4/35 dark:fill-room-4/50',
    stroke: 'stroke-room-4',
    swatch: 'bg-room-4',
    sample: 'bg-room-4/25 dark:bg-room-4/40 border-room-4',
    tint: 'bg-room-4/15 dark:bg-room-4/25',
  },
  'room-5': {
    fill: 'fill-room-5/25 dark:fill-room-5/40',
    fillHover: 'fill-room-5/32 dark:fill-room-5/50',
    fillStrong: 'fill-room-5/35 dark:fill-room-5/50',
    stroke: 'stroke-room-5',
    swatch: 'bg-room-5',
    sample: 'bg-room-5/25 dark:bg-room-5/40 border-room-5',
    tint: 'bg-room-5/15 dark:bg-room-5/25',
  },
  'room-6': {
    fill: 'fill-room-6/25 dark:fill-room-6/40',
    fillHover: 'fill-room-6/32 dark:fill-room-6/50',
    fillStrong: 'fill-room-6/35 dark:fill-room-6/50',
    stroke: 'stroke-room-6',
    swatch: 'bg-room-6',
    sample: 'bg-room-6/25 dark:bg-room-6/40 border-room-6',
    tint: 'bg-room-6/15 dark:bg-room-6/25',
  },
  'room-7': {
    fill: 'fill-room-7/25 dark:fill-room-7/40',
    fillHover: 'fill-room-7/32 dark:fill-room-7/50',
    fillStrong: 'fill-room-7/35 dark:fill-room-7/50',
    stroke: 'stroke-room-7',
    swatch: 'bg-room-7',
    sample: 'bg-room-7/25 dark:bg-room-7/40 border-room-7',
    tint: 'bg-room-7/15 dark:bg-room-7/25',
  },
  'room-8': {
    fill: 'fill-room-8/25 dark:fill-room-8/40',
    fillHover: 'fill-room-8/32 dark:fill-room-8/50',
    fillStrong: 'fill-room-8/35 dark:fill-room-8/50',
    stroke: 'stroke-room-8',
    swatch: 'bg-room-8',
    sample: 'bg-room-8/25 dark:bg-room-8/40 border-room-8',
    tint: 'bg-room-8/15 dark:bg-room-8/25',
  },
}

export function isRoomColorToken(value: unknown): value is RoomColorToken {
  return typeof value === 'string' && (ROOM_COLOR_TOKENS as readonly string[]).includes(value)
}

/** The classes of a room color (an unknown token falls back to the first color). */
export function roomColorClasses(token: string): RoomColorClasses {
  return CLASSES[isRoomColorToken(token) ? token : 'room-1']
}
