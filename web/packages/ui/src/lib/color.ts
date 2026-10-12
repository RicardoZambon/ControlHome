/**
 * Converts the OKLCH token values (tokens.css) to sRGB for consumers that cannot parse CSS Color 4,
 * such as ECharts. It never defines colors: every input comes from a token.
 */

export interface Oklch {
  /** Lightness, 0..1. */
  l: number
  /** Chroma, 0..~0.4. */
  c: number
  /** Hue in degrees. */
  h: number
  /** Opacity, 0..1. */
  alpha: number
}

/** sRGB channels 0..255 (gamma-encoded, rounded and clamped to the gamut) and opacity 0..1. */
export interface Rgba {
  r: number
  g: number
  b: number
  alpha: number
}

const NUMBER = String.raw`[+-]?(?:\d+\.?\d*|\.\d+)`
const OKLCH_VALUE = new RegExp(
  String.raw`^oklch\(\s*(${NUMBER})(%?)\s+(${NUMBER})(%?)\s+(${NUMBER})(?:deg)?\s*(?:\/\s*(${NUMBER})(%?)\s*)?\)$`,
  'i',
)

/** Parses `oklch(L C H)` / `oklch(L C H / A)` (numbers or percentages); null for anything else. */
export function parseOklch(value: string): Oklch | null {
  const match = OKLCH_VALUE.exec(value.trim())
  if (!match) return null
  const [, l, lPercent, c, cPercent, h, alpha, alphaPercent] = match
  return {
    l: lPercent ? Number(l) / 100 : Number(l),
    // CSS Color 4: 100% chroma is 0.4.
    c: cPercent ? (Number(c) / 100) * 0.4 : Number(c),
    h: Number(h),
    alpha: alpha === undefined ? 1 : alphaPercent ? Number(alpha) / 100 : Number(alpha),
  }
}

function round(value: number, digits: number): number {
  const factor = 10 ** digits
  return Math.round(value * factor) / factor
}

/** OKLCH -> sRGB (Björn Ottosson's OKLab matrices, then the sRGB transfer function). */
export function oklchToRgba(value: string): Rgba | null {
  const color = parseOklch(value)
  if (!color) return null
  const a = color.c * Math.cos((color.h * Math.PI) / 180)
  const b = color.c * Math.sin((color.h * Math.PI) / 180)
  const l = (color.l + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const m = (color.l - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s = (color.l - 0.0894841775 * a - 1.291485548 * b) ** 3
  const linear = [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ]
  const [red, green, blue] = linear.map((channel) => {
    const v = Math.min(1, Math.max(0, channel))
    const encoded = v <= 0.0031308 ? 12.92 * v : 1.055 * v ** (1 / 2.4) - 0.055
    return Math.round(encoded * 255)
  }) as [number, number, number]
  return { r: red, g: green, b: blue, alpha: round(Math.min(1, Math.max(0, color.alpha)), 4) }
}

/**
 * Formats an OKLCH value as `#rrggbb` (opaque) or `#rrggbbaa` (translucent). `alpha` overrides the opacity.
 * Values that are not OKLCH (`transparent`, `var(...)`, an empty string) are returned unchanged.
 */
export function toHexColor(value: string, alpha?: number): string {
  const rgba = oklchToRgba(value)
  if (!rgba) return value
  const opacity = alpha ?? rgba.alpha
  const channels = [rgba.r, rgba.g, rgba.b]
  if (opacity < 1) channels.push(Math.round(Math.min(1, Math.max(0, opacity)) * 255))
  return `#${channels.map((channel) => channel.toString(16).padStart(2, '0')).join('')}`
}
