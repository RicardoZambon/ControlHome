import type { Tone } from './deviceState'

/**
 * Classes for each state tone, spelled out so Tailwind generates them. State colors are used for dots, icons, borders
 * and glows (they reach 3:1 against the surfaces); words stay in the foreground colors, or sit on a solid state fill
 * with its `-foreground` pair (4.5:1).
 */
export interface ToneClasses {
  /** A small dot. */
  dot: string
  /** An icon on a card. */
  icon: string
  /** A soft icon bubble behind `icon`. */
  bubble: string
  /** A solid fill with readable text on it. */
  solid: string
  /** A border hint. */
  border: string
  /** The start of a tile's glow (a radial gradient). */
  glow: string
}

export const TONES: Record<Tone, ToneClasses> = {
  on: {
    dot: 'bg-state-on',
    icon: 'text-state-on',
    bubble: 'bg-state-on/15',
    solid: 'bg-state-on text-state-on-foreground',
    border: 'border-state-on/45',
    glow: 'from-state-on/12',
  },
  off: {
    dot: 'bg-state-off',
    icon: 'text-muted-foreground',
    bubble: 'bg-muted',
    solid: 'bg-state-off text-state-off-foreground',
    border: 'border-border',
    glow: 'from-transparent',
  },
  offline: {
    dot: 'border-2 border-state-offline bg-transparent',
    icon: 'text-state-offline',
    bubble: 'bg-state-offline/10',
    solid: 'bg-state-offline text-state-offline-foreground',
    border: 'border-dashed border-state-offline/50',
    glow: 'from-transparent',
  },
  warn: {
    dot: 'bg-state-warn',
    icon: 'text-state-warn',
    bubble: 'bg-state-warn/15',
    solid: 'bg-state-warn text-state-warn-foreground',
    border: 'border-state-warn/50',
    glow: 'from-state-warn/12',
  },
  danger: {
    dot: 'bg-state-danger',
    icon: 'text-state-danger',
    bubble: 'bg-state-danger/15',
    solid: 'bg-state-danger text-state-danger-foreground',
    border: 'border-state-danger/60',
    glow: 'from-state-danger/15',
  },
  info: {
    dot: 'bg-state-info',
    icon: 'text-state-info',
    bubble: 'bg-state-info/15',
    solid: 'bg-state-info text-state-info-foreground',
    border: 'border-state-info/45',
    glow: 'from-state-info/12',
  },
  energy: {
    dot: 'bg-state-energy',
    icon: 'text-state-energy',
    bubble: 'bg-state-energy/15',
    solid: 'bg-state-energy text-state-energy-foreground',
    border: 'border-state-energy/45',
    glow: 'from-state-energy/12',
  },
  neutral: {
    dot: 'bg-muted-foreground',
    icon: 'text-muted-foreground',
    bubble: 'bg-muted',
    solid: 'bg-secondary text-secondary-foreground',
    border: 'border-border',
    glow: 'from-transparent',
  },
}
