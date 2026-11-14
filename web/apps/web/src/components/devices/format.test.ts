import { describe, expect, it } from 'vitest'
import { createAppI18n } from '../../i18n'
import {
  UNIT_SPACE,
  formatMeasurement,
  formatStateValue,
  measurement,
  translatedValue,
  valueLabel,
} from './format'

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

describe.each([
  ['en', 'Replace', 'Off', 'Warm'],
  ['pt-BR', 'Trocar', 'Desligado', 'Morna'],
] as const)('vendor values (%s)', (locale, replace, off, warm) => {
  it('translate common tokens when the attribute has no wording of its own, and keep free text as it is', () => {
    const { t, te } = createAppI18n({ locale }).global
    expect(valueLabel('waterFilterStatus', 'replace', t, te)).toBe(replace)
    expect(valueLabel('washerWaterTemperature', 'warm', t, te)).toBe(warm)
    // The attribute's own wording wins over the shared one ("lockState" says "Unlocked" its way).
    expect(valueLabel('lockState', 'unlocked', t, te)).toBe(t('device.values.lockState.unlocked'))
    expect(valueLabel('washerCycle', 'Table_00_Course_01', t, te)).toBe('Table 00 course 01')
    expect(translatedValue('tvChannelName', 'PlayStation 4', t, te)).toBeUndefined()
    const text = {
      attributeKey: 'refrigerationDefrost',
      schema: { type: 'string' as const, maxLength: null },
      locale,
      t,
      te,
    }
    expect(formatStateValue({ kind: 'string', s: 'off' }, text)).toBe(off)
    expect(formatStateValue({ kind: 'string', s: 'Samsung QLED' }, text)).toBe('Samsung QLED')
  })
})
