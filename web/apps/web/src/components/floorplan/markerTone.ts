import type { EndpointSummary, Tone } from '../devices/deviceState'

/**
 * The state of a marker on the floor plan from its endpoints' summaries: an alarm or a warning on any of them first,
 * offline only when every endpoint is, then the first one that is on or running, else the first. A marker of one
 * endpoint simply shows that endpoint's state.
 */
export function combineSummaries(summaries: readonly EndpointSummary[]): EndpointSummary {
  if (summaries.length === 0) return { tone: 'neutral', label: '', details: [], active: false }
  const first = summaries[0]!
  return (
    summaries.find((summary) => summary.tone === 'danger') ??
    summaries.find((summary) => summary.tone === 'warn') ??
    (summaries.every((summary) => summary.tone === 'offline') ? first : undefined) ??
    summaries.find((summary) => summary.active) ??
    first
  )
}

/** SVG classes of a marker for each tone: the state ring, the soft halo of an active device and the icon. */
export interface MarkerToneClasses {
  ring: string
  halo: string
  icon: string
  /** The solid dot of a dashboard preview. */
  dot: string
  /** A dashed ring: the device can't be reached. */
  dashed: boolean
}

export const MARKER_TONES: Record<Tone, MarkerToneClasses> = {
  on: {
    ring: 'stroke-state-on',
    halo: 'fill-state-on/20',
    icon: 'text-state-on',
    dot: 'fill-state-on',
    dashed: false,
  },
  off: {
    ring: 'stroke-state-off',
    halo: 'fill-transparent',
    icon: 'text-muted-foreground',
    dot: 'fill-card',
    dashed: false,
  },
  offline: {
    ring: 'stroke-state-offline',
    halo: 'fill-transparent',
    icon: 'text-state-offline',
    dot: 'fill-card',
    dashed: true,
  },
  warn: {
    ring: 'stroke-state-warn',
    halo: 'fill-state-warn/20',
    icon: 'text-state-warn',
    dot: 'fill-state-warn',
    dashed: false,
  },
  danger: {
    ring: 'stroke-state-danger',
    halo: 'fill-state-danger/25',
    icon: 'text-state-danger',
    dot: 'fill-state-danger',
    dashed: false,
  },
  info: {
    ring: 'stroke-state-info',
    halo: 'fill-state-info/20',
    icon: 'text-state-info',
    dot: 'fill-state-info',
    dashed: false,
  },
  energy: {
    ring: 'stroke-state-energy',
    halo: 'fill-state-energy/25',
    icon: 'text-state-energy',
    dot: 'fill-state-energy',
    dashed: false,
  },
  neutral: {
    ring: 'stroke-muted-foreground/60',
    halo: 'fill-transparent',
    icon: 'text-muted-foreground',
    dot: 'fill-card',
    dashed: false,
  },
}

/** Marker radius in screen pixels: on the plan, on touch screens, and on dashboard previews. */
export const MARKER_RADIUS_PX = { fine: 15, coarse: 18, compact: 6 } as const

/** How far around a marker (screen px) a tap still opens it. */
export const MARKER_HIT_SLOP_PX = 6
