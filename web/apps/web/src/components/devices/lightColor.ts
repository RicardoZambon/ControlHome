/**
 * The color a bulb shows, from its `color` capability, as design-token classes (`--spectrum-*`, `--kelvin-*` in
 * tokens.css): a value between two token stops is drawn as the lower stop with the upper one on top at the matching
 * opacity. Class names are spelled out in full so Tailwind generates them.
 */

export const SPECTRUM_STOPS = [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330] as const
export type SpectrumStop = (typeof SPECTRUM_STOPS)[number]

interface StopClasses {
  /** Solid fill. */
  bg: string
  /** Gradient end (the saturation track). */
  to: string
  /** Soft glow behind a tile (gradient start). */
  glow: string
}

const SPECTRUM: Record<SpectrumStop, StopClasses> = {
  0: { bg: 'bg-spectrum-0', to: 'to-spectrum-0', glow: 'from-spectrum-0/25' },
  30: { bg: 'bg-spectrum-30', to: 'to-spectrum-30', glow: 'from-spectrum-30/25' },
  60: { bg: 'bg-spectrum-60', to: 'to-spectrum-60', glow: 'from-spectrum-60/25' },
  90: { bg: 'bg-spectrum-90', to: 'to-spectrum-90', glow: 'from-spectrum-90/25' },
  120: { bg: 'bg-spectrum-120', to: 'to-spectrum-120', glow: 'from-spectrum-120/25' },
  150: { bg: 'bg-spectrum-150', to: 'to-spectrum-150', glow: 'from-spectrum-150/25' },
  180: { bg: 'bg-spectrum-180', to: 'to-spectrum-180', glow: 'from-spectrum-180/25' },
  210: { bg: 'bg-spectrum-210', to: 'to-spectrum-210', glow: 'from-spectrum-210/25' },
  240: { bg: 'bg-spectrum-240', to: 'to-spectrum-240', glow: 'from-spectrum-240/25' },
  270: { bg: 'bg-spectrum-270', to: 'to-spectrum-270', glow: 'from-spectrum-270/25' },
  300: { bg: 'bg-spectrum-300', to: 'to-spectrum-300', glow: 'from-spectrum-300/25' },
  330: { bg: 'bg-spectrum-330', to: 'to-spectrum-330', glow: 'from-spectrum-330/25' },
}

export const KELVIN_STOPS = [
  { kelvin: 2200, bg: 'bg-kelvin-warm', glow: 'from-kelvin-warm/30' },
  { kelvin: 4500, bg: 'bg-kelvin-neutral', glow: 'from-kelvin-neutral/30' },
  { kelvin: 7500, bg: 'bg-kelvin-cool', glow: 'from-kelvin-cool/30' },
] as const

/** A color drawn as two stacked token fills. */
export interface ColorLayers {
  base: string
  over: string
  /** Opacity of `over` (0–1). */
  mix: number
  /** The closest stop's glow class. */
  glow: string
}

/** Hue in degrees (any value; wraps around) as token layers. */
export function hueLayers(hue: number): ColorLayers {
  const wrapped = ((hue % 360) + 360) % 360
  const index = Math.floor(wrapped / 30) % SPECTRUM_STOPS.length
  const lower = SPECTRUM_STOPS[index]!
  const upper = SPECTRUM_STOPS[(index + 1) % SPECTRUM_STOPS.length]!
  const mix = (wrapped - lower) / 30
  return {
    base: SPECTRUM[lower].bg,
    over: SPECTRUM[upper].bg,
    mix,
    glow: SPECTRUM[mix < 0.5 ? lower : upper].glow,
  }
}

/** The saturation track's end for a hue (the closest stop). */
export function hueTrackEnd(hue: number): string {
  const wrapped = ((hue % 360) + 360) % 360
  const nearest = SPECTRUM_STOPS[Math.round(wrapped / 30) % SPECTRUM_STOPS.length]!
  return SPECTRUM[nearest].to
}

/** A color temperature in kelvin as token layers (warmer than 2200 K stays warm, cooler than 7500 K stays cool). */
export function kelvinLayers(kelvin: number): ColorLayers {
  const stops = KELVIN_STOPS
  if (kelvin <= stops[0].kelvin) return { base: stops[0].bg, over: stops[0].bg, mix: 0, glow: stops[0].glow }
  const last = stops[stops.length - 1]!
  if (kelvin >= last.kelvin) return { base: last.bg, over: last.bg, mix: 0, glow: last.glow }
  const index = kelvin < stops[1].kelvin ? 0 : 1
  const lower = stops[index]!
  const upper = stops[index + 1]!
  const mix = (kelvin - lower.kelvin) / (upper.kelvin - lower.kelvin)
  return { base: lower.bg, over: upper.bg, mix, glow: (mix < 0.5 ? lower : upper).glow }
}

/** What a color light shows: a hue (with saturation), or a white of some temperature. */
export type LightColor =
  { mode: 'color'; hue: number; saturation: number } | { mode: 'white'; kelvin: number }

/**
 * The light's current color from its attributes. The catalogue has no "color mode", so a saturated hue counts as
 * color and anything else as white (when a temperature is known).
 */
export function lightColorOf(values: {
  hue?: number
  saturation?: number
  colorTemperature?: number
}): LightColor | undefined {
  const { hue, saturation, colorTemperature } = values
  if (hue !== undefined && (saturation ?? 100) > 5)
    return { mode: 'color', hue, saturation: saturation ?? 100 }
  if (colorTemperature !== undefined) return { mode: 'white', kelvin: colorTemperature }
  if (hue !== undefined) return { mode: 'color', hue, saturation: saturation ?? 0 }
  return undefined
}

/** Layers of a light color (saturation is drawn by `ColorSwatch` as white on top). */
export function lightLayers(color: LightColor): ColorLayers {
  return color.mode === 'color' ? hueLayers(color.hue) : kelvinLayers(color.kelvin)
}
