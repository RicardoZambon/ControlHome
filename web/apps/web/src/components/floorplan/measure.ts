import { areaCm2, type Point } from './geometry'

const CM2_PER_M2 = 10_000

/** A room's area in m² (from its outline, so drafts show it too). */
export function roomAreaM2(polygon: readonly Point[]): number {
  return areaCm2(polygon) / CM2_PER_M2
}

/** "50 cm" below a metre, "4.5 m" from there on (at most two decimals). */
export function formatLength(cm: number, locale: string): string {
  if (Math.abs(cm) < 100) {
    return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(cm)} cm`
  }
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(cm / 100)} m`
}

/** "9 × 8 m": a room's width and depth in metres (at most two decimals), the way people size rooms. */
export function formatDimensions(widthCm: number, heightCm: number, locale: string): string {
  const metres = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 })
  return `${metres.format(widthCm / 100)} × ${metres.format(heightCm / 100)} m`
}

/** "18.5 m²". */
export function formatArea(m2: number, locale: string): string {
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(m2)} m²`
}
