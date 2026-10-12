// Tests of the color conversion itself: the literals below are inputs and expected outputs (color-ok lines).
import { describe, expect, it } from 'vitest'
import { oklchToRgba, parseOklch, toHexColor } from './color'

describe('parseOklch', () => {
  it('reads lightness, chroma, hue and alpha', () => {
    expect(parseOklch('oklch(0.52 0.085 195)')).toEqual({ l: 0.52, c: 0.085, h: 195, alpha: 1 }) // color-ok
    expect(parseOklch('oklch(0.22 0.025 250 / 0.6)')).toEqual({ l: 0.22, c: 0.025, h: 250, alpha: 0.6 }) // color-ok
  })

  it('accepts percentages and a deg unit', () => {
    expect(parseOklch('oklch(50% 25% 120deg / 40%)')).toEqual({ l: 0.5, c: 0.1, h: 120, alpha: 0.4 }) // color-ok
  })

  it('returns null for anything else', () => {
    expect(parseOklch('transparent')).toBeNull()
    expect(parseOklch('var(--primary)')).toBeNull()
    expect(parseOklch('0.75rem')).toBeNull()
  })
})

describe('oklchToRgba', () => {
  it.each([
    ['oklch(1 0 0)', [255, 255, 255]], // color-ok
    ['oklch(0 0 0)', [0, 0, 0]], // color-ok
    ['oklch(0.627955 0.257683 29.2339)', [255, 0, 0]], // color-ok
    ['oklch(0.86644 0.294827 142.4953)', [0, 255, 0]], // color-ok
    ['oklch(0.452014 0.313214 264.052)', [0, 0, 255]], // color-ok
  ])('converts %s to sRGB', (value, [r, g, b]) => {
    expect(oklchToRgba(value)).toEqual({ r, g, b, alpha: 1 })
  })

  it('clamps colors outside the sRGB gamut', () => {
    const { r, g, b } = oklchToRgba('oklch(0.7 0.4 150)')! // color-ok
    for (const channel of [r, g, b]) {
      expect(channel).toBeGreaterThanOrEqual(0)
      expect(channel).toBeLessThanOrEqual(255)
    }
  })

  it('returns null for values that are not OKLCH', () => {
    expect(oklchToRgba('var(--primary)')).toBeNull()
  })
})

describe('toHexColor', () => {
  it('formats opaque colors as #rrggbb and translucent ones as #rrggbbaa', () => {
    expect(toHexColor('oklch(1 0 0)')).toBe('#ffffff') // color-ok
    expect(toHexColor('oklch(0 0 0 / 0.5)')).toBe('#00000080') // color-ok
  })

  it('applies an alpha override', () => {
    expect(toHexColor('oklch(1 0 0)', 0.2)).toBe('#ffffff33') // color-ok
  })

  it('returns values it cannot convert unchanged', () => {
    expect(toHexColor('transparent')).toBe('transparent')
    expect(toHexColor('')).toBe('')
  })
})
