import { formatTimeZoneName, timeZoneOffset } from './format'

export const DEFAULT_TIME_ZONE = 'America/Sao_Paulo'
export const DEFAULT_CURRENCY = 'BRL'

/** IANA time zones known to this browser (plus UTC and the defaults, which some engines leave out). */
export function supportedTimeZones(): string[] {
  let zones: string[] = []
  try {
    zones = Intl.supportedValuesOf('timeZone')
  } catch {
    zones = []
  }
  const extra = [DEFAULT_TIME_ZONE, 'UTC', browserTimeZone()].filter((zone): zone is string => !!zone)
  return [...new Set([...zones, ...extra])].sort((a, b) => a.localeCompare(b))
}

/** The browser's own time zone, if it reports one. */
export function browserTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined
  } catch {
    return undefined
  }
}

export interface Option {
  value: string
  label: string
  hint?: string
  keywords?: string
}

/** Time zones for a searchable select: "America / Sao Paulo" with its current UTC offset. */
export function timeZoneOptions(now = new Date()): Option[] {
  return supportedTimeZones().map((zone) => {
    const offset = timeZoneOffset(zone, now)
    return { value: zone, label: formatTimeZoneName(zone), hint: offset, keywords: offset.replace(':', '') }
  })
}

/** ISO 4217 currencies with their names in `locale` ("BRL — Real brasileiro"). */
export function currencyOptions(locale: string): Option[] {
  let codes: string[] = []
  try {
    codes = Intl.supportedValuesOf('currency')
  } catch {
    codes = []
  }
  if (!codes.includes(DEFAULT_CURRENCY)) codes = [DEFAULT_CURRENCY, ...codes]
  let names: Intl.DisplayNames | undefined
  try {
    names = new Intl.DisplayNames([locale], { type: 'currency' })
  } catch {
    names = undefined
  }
  return codes.map((code) => {
    const name = names?.of(code)
    return { value: code, label: name && name !== code ? `${code} — ${name}` : code, keywords: code }
  })
}

/** The symbol of a currency in `locale` ("R$", "US$"), or the code. */
export function currencySymbol(currency: string, locale: string): string {
  try {
    return (
      new Intl.NumberFormat(locale, { style: 'currency', currency, currencyDisplay: 'narrowSymbol' })
        .formatToParts(0)
        .find((part) => part.type === 'currency')?.value ?? currency
    )
  } catch {
    return currency
  }
}
