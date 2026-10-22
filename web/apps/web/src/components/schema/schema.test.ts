import type { ValueSchema } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import {
  allowedValues,
  constraintFor,
  defaultValueFor,
  fieldsFromFieldSchemas,
  humanize,
  numberBounds,
  snapNumber,
  validateFields,
  validateValue,
} from './schema'

const target = { type: 'number', min: 5, max: 35, step: 0.5, unit: '°C' } as const
const modes: ValueSchema = { type: 'enum', values: ['off', 'cool', 'heat', 'auto'] }

describe('numberBounds', () => {
  it('narrows the schema with the constraint, whose step counts from its own minimum', () => {
    expect(numberBounds(target, { min: 16, max: 30, step: 1 })).toEqual({
      min: 16,
      max: 30,
      step: 1,
      origin: 16,
      integer: false,
      unit: '°C',
    })
    // A constraint never widens the schema.
    expect(numberBounds(target, { min: 0, max: 50 })).toMatchObject({ min: 5, max: 35, step: 0.5, origin: 5 })
  })
})

describe('snapNumber', () => {
  it('rounds onto the step grid and clamps into the range', () => {
    const bounds = numberBounds(target)
    expect(snapNumber(22.3, bounds)).toBe(22.5)
    expect(snapNumber(0.1 * 3 + 21, bounds)).toBe(21.5)
    expect(snapNumber(100, bounds)).toBe(35)
    expect(snapNumber(-3, bounds)).toBe(5)
  })
})

describe('validateValue (mirrors ValueSchemaValidator)', () => {
  it.each([
    [{ type: 'bool' }, true, null],
    [{ type: 'bool' }, 'yes', 'invalid_type'],
    [target, 22.5, null],
    [target, 22.3, 'not_on_step'],
    [target, 40, 'out_of_range'],
    [target, '22', 'invalid_type'],
    [{ type: 'number', integer: true }, 2.5, 'invalid_type'],
    [modes, 'cool', null],
    [modes, 'turbo', 'not_allowed'],
    [{ type: 'string', maxLength: 3 }, 'abcd', 'too_long'],
    [{ type: 'flags', flags: ['a', 'b'] }, ['a', 'b'], null],
    [{ type: 'flags', flags: ['a', 'b'] }, ['a', 'a'], 'not_allowed'],
    [{ type: 'flags', flags: ['a', 'b'] }, ['c'], 'not_allowed'],
    [{ type: 'momentary', events: ['single'] }, 'double', 'not_allowed'],
    [{ type: 'json' }, { any: 'thing' }, null],
  ] as const)('%o with %o → %s', (schema, value, expected) => {
    expect(validateValue(schema as never, value)).toBe(expected)
  })

  it('treats an empty value as missing: an error only when required', () => {
    expect(validateValue(target, undefined)).toBeNull()
    expect(validateValue(target, undefined, { required: true })).toBe('required')
    expect(validateValue({ type: 'string' }, '', { required: true })).toBe('required')
  })

  it('applies the endpoint constraint (range, step and allowed values)', () => {
    expect(validateValue(target, 31, { constraint: { max: 30 } })).toBe('out_of_range')
    expect(validateValue(target, 22.5, { constraint: { min: 16, step: 1 } })).toBe('not_on_step')
    expect(validateValue(modes, 'auto', { constraint: { values: ['off', 'cool'] } })).toBe('not_allowed')
  })

  it('validates whole forms by field', () => {
    expect(
      validateFields(
        [
          { key: 'seed', label: 'Seed', schema: { type: 'number', min: 0, integer: true }, required: true },
          { key: 'preset', label: 'Preset', schema: { type: 'enum', values: ['basic', 'demo'] } },
        ],
        { seed: -1, preset: 'other' },
      ),
    ).toEqual({ seed: 'out_of_range', preset: 'not_allowed' })
  })
})

describe('constraintFor', () => {
  const constraints = {
    attributes: {
      mode: { values: ['off', 'cool', 'heat'] },
      targetTemperature: { min: 16, max: 30, step: 0.5, dependsOn: 'mode', cases: { heat: { max: 26 } } },
    },
  }

  it('reads an attribute constraint', () => {
    expect(constraintFor(constraints, 'mode')).toEqual({
      min: undefined,
      max: undefined,
      step: undefined,
      values: ['off', 'cool', 'heat'],
    })
    expect(constraintFor(constraints, 'fanMode')).toBeUndefined()
    expect(constraintFor(null, 'mode')).toBeUndefined()
  })

  it('applies the case selected by the current value of its dependsOn attribute', () => {
    const current = (mode: string) => (key: string) => (key === 'mode' ? mode : undefined)
    expect(constraintFor(constraints, 'targetTemperature', current('heat'))).toMatchObject({
      min: 16,
      max: 26,
      step: 0.5,
    })
    expect(constraintFor(constraints, 'targetTemperature', current('cool'))).toMatchObject({ max: 30 })
  })
})

describe('helpers', () => {
  it('gives sensible defaults and allowed values', () => {
    expect(defaultValueFor({ type: 'bool' })).toBe(false)
    expect(defaultValueFor(target, { min: 16 })).toBe(16)
    expect(defaultValueFor(modes, { values: ['cool', 'heat'] })).toBe('cool')
    expect(defaultValueFor({ type: 'flags', flags: ['a'] })).toEqual([])
    expect(allowedValues(modes, { values: ['heat', 'off'] })).toEqual(['off', 'heat'])
  })

  it('builds form fields from a connector config in the UI language', () => {
    const fields = fieldsFromFieldSchemas(
      [
        {
          key: 'token',
          label: { en: 'Token', ptBr: 'Token de acesso' },
          schema: { type: 'string' },
          required: true,
          secret: true,
          advanced: false,
          help: { en: 'From the app', ptBr: 'Do aplicativo' },
        },
      ],
      'pt-BR',
    )
    expect(fields).toEqual([
      {
        key: 'token',
        label: 'Token de acesso',
        schema: { type: 'string' },
        required: true,
        secret: true,
        advanced: false,
        help: 'Do aplicativo',
      },
    ])
  })

  it('humanizes keys', () => {
    expect(humanize('fanMode')).toBe('Fan mode')
    expect(humanize('remote_control')).toBe('Remote control')
    expect(humanize('armed_away')).toBe('Armed away')
  })
})
