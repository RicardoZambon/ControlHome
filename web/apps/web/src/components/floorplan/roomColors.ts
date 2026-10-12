/**
 * Room colors are design tokens (`--room-1` … `--room-8` in tokens.css, light and dark), never raw colors. The classes
 * are spelled out in full so Tailwind's scanner generates them (a class built at runtime, `fill-${token}`, would not
 * exist).
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
  /** Room fill at rest (translucent, so the grid and the background image show through). */
  fill: string
  /** Room fill when hovered or selected. */
  fillActive: string
  /** Outline in the solid token. */
  stroke: string
  /** A solid swatch (color pickers, legends). */
  swatch: string
  /** A soft tint for list rows and chips. */
  tint: string
}

const CLASSES: Record<RoomColorToken, RoomColorClasses> = {
  'room-1': {
    fill: 'fill-room-1/25',
    fillActive: 'fill-room-1/40',
    stroke: 'stroke-room-1',
    swatch: 'bg-room-1',
    tint: 'bg-room-1/15',
  },
  'room-2': {
    fill: 'fill-room-2/25',
    fillActive: 'fill-room-2/40',
    stroke: 'stroke-room-2',
    swatch: 'bg-room-2',
    tint: 'bg-room-2/15',
  },
  'room-3': {
    fill: 'fill-room-3/25',
    fillActive: 'fill-room-3/40',
    stroke: 'stroke-room-3',
    swatch: 'bg-room-3',
    tint: 'bg-room-3/15',
  },
  'room-4': {
    fill: 'fill-room-4/25',
    fillActive: 'fill-room-4/40',
    stroke: 'stroke-room-4',
    swatch: 'bg-room-4',
    tint: 'bg-room-4/15',
  },
  'room-5': {
    fill: 'fill-room-5/25',
    fillActive: 'fill-room-5/40',
    stroke: 'stroke-room-5',
    swatch: 'bg-room-5',
    tint: 'bg-room-5/15',
  },
  'room-6': {
    fill: 'fill-room-6/25',
    fillActive: 'fill-room-6/40',
    stroke: 'stroke-room-6',
    swatch: 'bg-room-6',
    tint: 'bg-room-6/15',
  },
  'room-7': {
    fill: 'fill-room-7/25',
    fillActive: 'fill-room-7/40',
    stroke: 'stroke-room-7',
    swatch: 'bg-room-7',
    tint: 'bg-room-7/15',
  },
  'room-8': {
    fill: 'fill-room-8/25',
    fillActive: 'fill-room-8/40',
    stroke: 'stroke-room-8',
    swatch: 'bg-room-8',
    tint: 'bg-room-8/15',
  },
}

export function isRoomColorToken(value: unknown): value is RoomColorToken {
  return typeof value === 'string' && (ROOM_COLOR_TOKENS as readonly string[]).includes(value)
}

/** The classes of a room color (an unknown token falls back to the first color). */
export function roomColorClasses(token: string): RoomColorClasses {
  return CLASSES[isRoomColorToken(token) ? token : 'room-1']
}
