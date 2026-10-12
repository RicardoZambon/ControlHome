// @vitest-environment node
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

type Oklch = readonly [l: number, c: number, h: number]
type Color = { lch: Oklch; alpha: number }
type Theme = Map<string, string>

const tokensCss = readFileSync(new URL('./tokens.css', import.meta.url), 'utf8')
const themeCss = readFileSync(new URL('./theme.css', import.meta.url), 'utf8')

function readBlock(selector: string): Theme {
  const start = tokensCss.indexOf(`${selector} {`)
  if (start < 0) throw new Error(`tokens.css has no "${selector}" block`)
  const body = tokensCss.slice(start, tokensCss.indexOf('}', start))
  const vars: Theme = new Map()
  for (const match of body.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) vars.set(match[1]!, match[2]!.trim())
  return vars
}

const light = readBlock(':root')
// `.dark` overrides colors only; anything it does not redefine (radius, black, white) comes from :root.
const dark: Theme = new Map([...light, ...readBlock('.dark')])
const themes = { light, dark } as const

/** Effect colors (overlay scrim, shadows) are translucent; every other color token is opaque. */
const effectTokens = [
  '--overlay',
  '--shadow-color-subtle',
  '--shadow-color',
  '--shadow-color-strong',
  '--shadow-highlight',
] as const
const shadowTokens = new Set<string>(effectTokens.filter((token) => token.startsWith('--shadow-')))
const nonColorTokens = new Set(['--radius'])

function parseColor(value: string): Color {
  const match = /^oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*(?:\/\s*([\d.]+)\s*)?\)$/.exec(value)
  if (!match) throw new Error(`"${value}" is not an OKLCH value with L, C, H and an optional alpha`)
  return {
    lch: [Number(match[1]), Number(match[2]), Number(match[3])],
    alpha: match[4] === undefined ? 1 : Number(match[4]),
  }
}

/** OKLCH -> linear-light sRGB (Björn Ottosson's OKLab matrices). */
function toLinearSrgb([l, c, h]: Oklch): [number, number, number] {
  const a = c * Math.cos((h * Math.PI) / 180)
  const b = c * Math.sin((h * Math.PI) / 180)
  const lc = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const mc = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const sc = (l - 0.0894841775 * a - 1.291485548 * b) ** 3
  return [
    4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc,
    -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc,
    -0.0041960863 * lc - 0.7034186147 * mc + 1.707614701 * sc,
  ]
}

function relativeLuminance(color: Oklch): number {
  const [r, g, b] = toLinearSrgb(color).map((v) => Math.min(1, Math.max(0, v))) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrast(theme: Theme, a: string, b: string): number {
  const ca = parseColor(theme.get(a)!)
  const cb = parseColor(theme.get(b)!)
  if (ca.alpha !== 1 || cb.alpha !== 1) throw new Error(`contrast needs opaque colors: ${a}, ${b}`)
  const la = relativeLuminance(ca.lch)
  const lb = relativeLuminance(cb.lch)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

const states = ['on', 'off', 'offline', 'warn', 'energy', 'danger', 'info'] as const
const rooms = [1, 2, 3, 4, 5, 6, 7, 8] as const

const requiredTokens = [
  '--background',
  '--foreground',
  '--card',
  '--card-foreground',
  '--popover',
  '--popover-foreground',
  '--primary',
  '--primary-foreground',
  '--secondary',
  '--secondary-foreground',
  '--muted',
  '--muted-foreground',
  '--accent',
  '--accent-foreground',
  '--destructive',
  '--destructive-foreground',
  '--border',
  '--input',
  '--ring',
  '--radius',
  '--sidebar',
  '--sidebar-foreground',
  '--sidebar-primary',
  '--sidebar-primary-foreground',
  '--sidebar-accent',
  '--sidebar-accent-foreground',
  '--sidebar-border',
  '--sidebar-ring',
  ...effectTokens,
  ...states.flatMap((s) => [`--state-${s}`, `--state-${s}-foreground`]),
  ...rooms.flatMap((n) => [`--room-${n}`, `--room-${n}-foreground`]),
]

/** Surfaces that text, inputs and focus rings sit on. */
const surfaces = ['--background', '--card', '--popover'] as const

// Text pairs: WCAG AA for normal text.
const textPairs: Array<[string, string]> = [
  ['--background', '--foreground'],
  ['--card', '--card-foreground'],
  ['--popover', '--popover-foreground'],
  ['--primary', '--primary-foreground'],
  ['--secondary', '--secondary-foreground'],
  ['--muted', '--muted-foreground'],
  ['--accent', '--accent-foreground'],
  ['--destructive', '--destructive-foreground'],
  ['--sidebar', '--sidebar-foreground'],
  ['--sidebar-primary', '--sidebar-primary-foreground'],
  ['--sidebar-accent', '--sidebar-accent-foreground'],
  ...states.map((s): [string, string] => [`--state-${s}`, `--state-${s}-foreground`]),
  ...rooms.map((n): [string, string] => [`--room-${n}`, `--room-${n}-foreground`]),
  // Colored text used directly on surfaces: muted copy, links, error messages.
  ...surfaces.flatMap((surface) =>
    ['--muted-foreground', '--primary', '--destructive'].map((fg): [string, string] => [surface, fg]),
  ),
  // Muted copy on tinted surfaces: sidebar group labels, highlighted menu items, inactive tabs.
  ['--sidebar', '--muted-foreground'],
  ['--sidebar-accent', '--muted-foreground'],
  ['--accent', '--muted-foreground'],
  ['--secondary', '--muted-foreground'],
]

// Non-text pairs (WCAG 1.4.11): input borders, focus rings, state and room colors against the surfaces.
const nonTextPairs: Array<[string, string]> = [
  ...surfaces.flatMap((surface) =>
    [
      '--input',
      '--ring',
      '--primary',
      ...states.map((s) => `--state-${s}`),
      ...rooms.map((n) => `--room-${n}`),
    ].map((fg): [string, string] => [surface, fg]),
  ),
  ['--sidebar', '--sidebar-ring'],
  ['--sidebar', '--sidebar-primary'],
  ['--sidebar', '--input'],
]

describe.each(Object.entries(themes))('%s theme tokens', (_name, theme) => {
  it('defines every required token', () => {
    const missing = requiredTokens.filter((token) => !theme.has(token))
    expect(missing).toEqual([])
  })

  it('uses OKLCH colors inside the sRGB gamut', () => {
    const outOfGamut = [...theme]
      .filter(([name]) => !nonColorTokens.has(name))
      .filter(([, value]) => toLinearSrgb(parseColor(value).lch).some((v) => v < -0.0005 || v > 1.0005))
      .map(([name, value]) => `${name}: ${value}`)
    expect(outOfGamut).toEqual([])
  })

  it('keeps colors opaque except the translucent effect tokens', () => {
    const wrongOpacity = [...theme]
      .filter(([name]) => !nonColorTokens.has(name))
      .filter(([name, value]) => {
        const { alpha } = parseColor(value)
        const isEffect = (effectTokens as readonly string[]).includes(name)
        return isEffect ? !(alpha > 0 && alpha < 1) : alpha !== 1
      })
      .map(([name, value]) => `${name}: ${value}`)
    expect(wrongOpacity).toEqual([])
  })

  it('makes shadows stronger from subtle to strong', () => {
    const alphas = ['--shadow-color-subtle', '--shadow-color', '--shadow-color-strong'].map(
      (token) => parseColor(theme.get(token)!).alpha,
    )
    expect([...alphas].sort((a, b) => a - b)).toEqual(alphas)
  })

  it.each(textPairs)('%s / %s reaches 4.5:1', (bg, fg) => {
    expect(contrast(theme, bg, fg)).toBeGreaterThanOrEqual(4.5)
  })

  it.each(nonTextPairs)('%s / %s reaches 3:1', (bg, fg) => {
    expect(contrast(theme, bg, fg)).toBeGreaterThanOrEqual(3)
  })
})

describe('room colors', () => {
  it.each(Object.entries(themes))('are distinct from each other in the %s theme', (_name, theme) => {
    // OKLab distance between every pair of room fills; 0.05 is clearly visible side by side.
    const toOklab = (token: string): [number, number, number] => {
      const [l, c, h] = parseColor(theme.get(token)!).lch
      return [l, c * Math.cos((h * Math.PI) / 180), c * Math.sin((h * Math.PI) / 180)]
    }
    const tooClose: string[] = []
    for (const a of rooms)
      for (const b of rooms.filter((n) => n > a)) {
        const [la, aa, ba] = toOklab(`--room-${a}`)
        const [lb, ab, bb] = toOklab(`--room-${b}`)
        if (Math.hypot(la - lb, aa - ab, ba - bb) < 0.05) tooClose.push(`room-${a}/room-${b}`)
      }
    expect(tooClose).toEqual([])
  })
})

describe('theme.css', () => {
  it('maps every color token to a Tailwind color', () => {
    const colorTokens = [...light.keys()].filter(
      (name) => !nonColorTokens.has(name) && !shadowTokens.has(name),
    )
    const unmapped = colorTokens.filter(
      (name) => !themeCss.includes(`--color-${name.slice(2)}: var(${name});`),
    )
    expect(unmapped).toEqual([])
  })

  it("removes Tailwind's default palette", () => {
    expect(themeCss).toContain('--color-*: initial;')
  })

  const shadowScales = {
    '--shadow': ['2xs', 'xs', 'sm', 'md', 'lg', 'xl', '2xl'],
    '--inset-shadow': ['2xs', 'xs', 'sm'],
    '--drop-shadow': ['xs', 'sm', 'md', 'lg', 'xl', '2xl'],
    '--text-shadow': ['2xs', 'xs', 'sm', 'md', 'lg'],
  }

  it.each(Object.keys(shadowScales))("replaces Tailwind's %s-* scale", (scale) => {
    expect(themeCss).toContain(`${scale}-*: initial;`)
  })

  // Every shadow size (and the deprecated bare `shadow`, `shadow-inner`, `drop-shadow`) takes its color from tokens.
  const shadowKeys = [
    ...Object.entries(shadowScales).flatMap(([scale, sizes]) => sizes.map((size) => `${scale}-${size}`)),
    '--shadow',
    '--shadow-inner',
    '--drop-shadow',
  ]

  it.each(shadowKeys)('%s uses only the shadow color tokens', (key) => {
    const match = new RegExp(`(?:^|\\s)${key}:([^;]+);`).exec(themeCss)
    expect(match, `${key} is not defined in theme.css`).not.toBeNull()
    const value = match![1]!
    const colors = [...value.matchAll(/var\((--[\w-]+)\)/g)].map((m) => m[1])
    expect(colors.length).toBeGreaterThan(0)
    expect(colors.every((name) => shadowTokens.has(name!))).toBe(true)
    expect(value).not.toMatch(/(?:rgba?|hsla?|oklch|#)/)
  })
})
