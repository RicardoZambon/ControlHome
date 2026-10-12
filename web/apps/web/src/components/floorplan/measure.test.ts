import { describe, expect, it } from 'vitest'
import { formatArea, formatLength, roomAreaM2 } from './measure'

describe('formatLength', () => {
  it('centimetres below a metre, metres from there on', () => {
    expect(formatLength(50, 'en')).toBe('50 cm')
    expect(formatLength(100, 'en')).toBe('1 m')
    expect(formatLength(450, 'en')).toBe('4.5 m')
    expect(formatLength(1234.5, 'en')).toBe('12.35 m')
    expect(formatLength(450, 'pt-BR')).toBe('4,5 m')
  })
})

describe('formatArea', () => {
  it('square metres with at most one decimal', () => {
    expect(formatArea(18.54, 'en')).toBe('18.5 m²')
    expect(formatArea(16, 'en')).toBe('16 m²')
    expect(formatArea(1234.56, 'pt-BR')).toBe('1.234,6 m²')
  })
})

describe('roomAreaM2', () => {
  it('is the polygon area in square metres', () => {
    expect(
      roomAreaM2([
        { x: 0, y: 0 },
        { x: 400, y: 0 },
        { x: 400, y: 300 },
        { x: 0, y: 300 },
      ]),
    ).toBe(12)
  })
})
