// @vitest-environment node
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

type Oklch = readonly [l: number, c: number, h: number]
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

function parseOklch(value: string): Oklch {
  const match = /^oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\)$/.exec(value)
  if (!match) throw new Error(`"${value}" is not an opaque OKLCH value with L, C and H`)
  return [Number(match[1]), Number(match[2]), Number(match[3])]
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
  const la = relativeLuminance(parseOklch(theme.get(a)!))
  const lb = relativeLuminance(parseOklch(theme.get(b)!))
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

const states = ['on', 'off', 'offline', 'warn', 'energy', 'danger', 'info'] as const

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
  '--sidebar-accent',
  '--sidebar-accent-foreground',
  '--sidebar-border',
  ...states.flatMap((s) => [`--state-${s}`, `--state-${s}-foreground`]),
]

const surfaces = ['--background', '--card'] as const

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
  // Colored text used directly on surfaces: muted copy, links, error messages.
  ...surfaces.flatMap((surface) =>
    ['--muted-foreground', '--primary', '--destructive'].map((fg): [string, string] => [surface, fg]),
  ),
]

// Non-text pairs (WCAG 1.4.11): input borders, focus rings and state icons against the surfaces.
const nonTextPairs: Array<[string, string]> = surfaces.flatMap((surface) =>
  ['--input', '--ring', ...states.map((s) => `--state-${s}`)].map((fg): [string, string] => [surface, fg]),
)

describe.each(Object.entries(themes))('%s theme tokens', (_name, theme) => {
  it('defines every required token', () => {
    const missing = requiredTokens.filter((token) => !theme.has(token))
    expect(missing).toEqual([])
  })

  it('uses opaque OKLCH colors inside the sRGB gamut', () => {
    const outOfGamut = [...theme]
      .filter(([name]) => name !== '--radius')
      .filter(([, value]) => toLinearSrgb(parseOklch(value)).some((v) => v < -0.0005 || v > 1.0005))
      .map(([name, value]) => `${name}: ${value}`)
    expect(outOfGamut).toEqual([])
  })

  it.each(textPairs)('%s / %s reaches 4.5:1', (bg, fg) => {
    expect(contrast(theme, bg, fg)).toBeGreaterThanOrEqual(4.5)
  })

  it.each(nonTextPairs)('%s / %s reaches 3:1', (bg, fg) => {
    expect(contrast(theme, bg, fg)).toBeGreaterThanOrEqual(3)
  })
})

describe('theme.css', () => {
  it('maps every color token to a Tailwind color', () => {
    const colorTokens = [...light.keys()].filter((name) => name !== '--radius')
    const unmapped = colorTokens.filter(
      (name) => !themeCss.includes(`--color-${name.slice(2)}: var(${name});`),
    )
    expect(unmapped).toEqual([])
  })

  it("removes Tailwind's default palette", () => {
    expect(themeCss).toContain('--color-*: initial;')
  })
})
