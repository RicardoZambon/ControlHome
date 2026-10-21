import type { StateValue, ValueSchema } from '@controlhome/api-client'
import { humanize } from '../schema/schema'
import { fromStateValue } from '../../lib/stateValue'

export type Translate = (key: string, named?: Record<string, unknown>) => string
export type HasTranslation = (key: string) => boolean

/** Translated label of a capability (`device.capabilities.<id>`), else its id humanized. */
export function capabilityLabel(id: string, t: Translate, te: HasTranslation): string {
  return te(`device.capabilities.${id}`) ? t(`device.capabilities.${id}`) : humanize(id)
}

/**
 * Translated label of an attribute or argument key: the capability's own wording first
 * (`device.attributesIn.<capability>.<key>`, e.g. the `power` capability's `power` is "Power use"), then
 * `device.attributes.<key>`, else the key humanized.
 */
export function attributeLabel(key: string, t: Translate, te: HasTranslation, capabilityId?: string): string {
  if (capabilityId && te(`device.attributesIn.${capabilityId}.${key}`))
    return t(`device.attributesIn.${capabilityId}.${key}`)
  return te(`device.attributes.${key}`) ? t(`device.attributes.${key}`) : humanize(key)
}

/** Translated label of a command (`device.commands.<name>`), else the name humanized. */
export function commandLabel(name: string, t: Translate, te: HasTranslation): string {
  return te(`device.commands.${name}`) ? t(`device.commands.${name}`) : humanize(name)
}

/** Translated label of an enum value or event of an attribute (`device.values.<key>.<value>`), else humanized. */
export function valueLabel(attributeKey: string, value: string, t: Translate, te: HasTranslation): string {
  const key = `device.values.${attributeKey}.${value}`
  return te(key) ? t(key) : humanize(value)
}

/** Fraction digits that suit a unit: temperatures to a tenth ("22 °C", "22,5 °C"), percentages and watt-hours whole. */
function digitsFor(unit: string | null | undefined, value: number): { min: number; max: number } {
  switch (unit) {
    case '°C':
      return { min: 0, max: 1 }
    case '%':
    case 'ppm':
    case 'ppb':
    case 'min':
    case 'K':
    case 'ms':
    case 'Wh':
      return { min: 0, max: 0 }
    case 'W':
    case 'µg/m³':
      return Math.abs(value) < 10 ? { min: 0, max: 1 } : { min: 0, max: 0 }
    default:
      return { min: 0, max: 2 }
  }
}

export interface Measurement {
  /** The number, formatted for the locale. */
  value: string
  /** The unit to show next to it (`kWh` for large energy totals). */
  unit: string
}

/** A measurement in a readable unit: Wh above 1 000 become kWh, everything else keeps its canonical unit. */
export function measurement(value: number, unit: string | null | undefined, locale: string): Measurement {
  if (unit === 'Wh' && Math.abs(value) >= 1000) {
    const kwh = value / 1000
    return {
      value: new Intl.NumberFormat(locale, {
        minimumFractionDigits: kwh < 100 ? 2 : 1,
        maximumFractionDigits: kwh < 100 ? 2 : 1,
      }).format(kwh),
      unit: 'kWh',
    }
  }
  const digits = digitsFor(unit, value)
  return {
    value: new Intl.NumberFormat(locale, {
      minimumFractionDigits: digits.min,
      maximumFractionDigits: digits.max,
    }).format(value),
    unit: unit ?? '',
  }
}

/** Between a number and its unit: a no-break space, so "24 °C" never wraps between the two. */
export const UNIT_SPACE = ' '

/**
 * "22,5 °C", "1,25 kWh" (with a no-break space before the unit), and "80%" (percent sticks to its number, as Intl's
 * percent style writes it in English and in Brazilian Portuguese).
 */
export function formatMeasurement(value: number, unit: string | null | undefined, locale: string): string {
  const { value: number, unit: shown } = measurement(value, unit, locale)
  if (!shown) return number
  return shown === '°' || shown === '%' ? `${number}${shown}` : `${number}${UNIT_SPACE}${shown}`
}

/** Any attribute value as text (the attributes table, read-only generic values). */
export function formatStateValue(
  value: Partial<StateValue> | undefined,
  options: {
    attributeKey: string
    schema?: ValueSchema
    unit?: string | null
    locale: string
    t: Translate
    te: HasTranslation
  },
): string {
  const plain = fromStateValue(value)
  const { t, te, locale } = options
  if (plain === undefined) return t('device.noValue')
  if (typeof plain === 'boolean') {
    // "On" / "Off", "Open" / "Closed" where the attribute has words of its own.
    const key = `device.values.${options.attributeKey}.${plain}`
    return te(key) ? t(key) : plain ? t('schema.true') : t('schema.false')
  }
  if (typeof plain === 'number') {
    const unit = options.unit ?? (options.schema?.type === 'number' ? options.schema.unit : undefined)
    return formatMeasurement(plain, unit, locale)
  }
  if (typeof plain === 'string') {
    if (te(`device.values.${options.attributeKey}.${plain}`))
      return t(`device.values.${options.attributeKey}.${plain}`)
    return options.schema?.type === 'enum' || options.schema?.type === 'momentary' ? humanize(plain) : plain
  }
  if (Array.isArray(plain) && plain.every((entry) => typeof entry === 'string'))
    return plain.length > 0 ? plain.join(', ') : t('schema.noFlags')
  return JSON.stringify(plain)
}
