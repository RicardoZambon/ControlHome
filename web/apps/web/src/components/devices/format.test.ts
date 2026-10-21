import { describe, expect, it } from 'vitest'
import { UNIT_SPACE, formatMeasurement, measurement } from './format'

const wh = (number: string) => `${number}${UNIT_SPACE}Wh`

describe('measurements', () => {
  it('writes watt-hours whole once they reach ten, and small amounts with a decimal instead of "0 Wh"', () => {
    expect(formatMeasurement(13.4, 'Wh', 'en')).toBe(wh('13'))
    expect(formatMeasurement(4.36, 'Wh', 'en')).toBe(wh('4.4'))
    expect(formatMeasurement(0.4, 'Wh', 'pt-BR')).toBe(wh('0,4'))
    expect(formatMeasurement(0.04, 'Wh', 'en')).toBe(wh('0.04'))
    expect(formatMeasurement(0, 'Wh', 'en')).toBe(wh('0'))
  })

  it('switches to kWh from 1 000 Wh', () => {
    expect(measurement(1250, 'Wh', 'en')).toEqual({ value: '1.25', unit: 'kWh' })
    expect(measurement(125_000, 'Wh', 'pt-BR')).toEqual({ value: '125,0', unit: 'kWh' })
  })
})
