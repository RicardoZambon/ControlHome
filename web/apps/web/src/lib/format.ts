export type DayPart = 'morning' | 'afternoon' | 'evening' | 'night'

/** The hour (0–23) of `date` in `timeZone`; the browser's zone when the id is unknown. */
export function hourIn(date: Date, timeZone?: string): number {
  const format = (zone?: string) =>
    new Intl.DateTimeFormat('en-US', { hour: 'numeric', hourCycle: 'h23', timeZone: zone }).format(date)
  let hour: string
  try {
    hour = format(timeZone)
  } catch {
    hour = format()
  }
  return Number.parseInt(hour, 10) % 24
}

/** Morning 5–11, afternoon 12–17, evening 18–21, night 22–4 (in the home's time zone). */
export function dayPartOf(date: Date, timeZone?: string): DayPart {
  const hour = hourIn(date, timeZone)
  if (hour >= 5 && hour < 12) return 'morning'
  if (hour >= 12 && hour < 18) return 'afternoon'
  if (hour >= 18 && hour < 22) return 'evening'
  return 'night'
}

const RELATIVE_UNITS: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
]

/**
 * "3 hours ago" / "há 3 horas". Under a minute: `justNow` when given ("Just now"), else Intl's "now" / "agora". Only
 * ever the past: a time ahead of `now` (two clocks apart) is "just now" too, never "in 4 hours".
 */
export function formatRelative(value: string | Date, now: Date, locale: string, justNow?: string): string {
  const date = typeof value === 'string' ? new Date(value) : value
  const seconds = Math.min(0, Math.round((date.getTime() - now.getTime()) / 1000))
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  for (const [unit, size] of RELATIVE_UNITS) {
    if (Math.abs(seconds) >= size) return format.format(Math.trunc(seconds / size), unit)
  }
  return justNow ?? format.format(0, 'second')
}

function dayKey(date: Date, timeZone?: string): string {
  const format = (zone?: string) =>
    new Intl.DateTimeFormat('en-CA', {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      timeZone: zone,
    }).format(date)
  try {
    return format(timeZone)
  } catch {
    return format()
  }
}

/**
 * A moment as a clock shows it: the time of day when it is today ("06:40"), with the date on other days
 * ("11 de out., 06:40"), in the home's time zone (the browser's for an unknown id).
 */
export function formatClock(value: string | Date, now: Date, locale: string, timeZone?: string): string {
  const date = typeof value === 'string' ? new Date(value) : value
  const today = dayKey(date, timeZone) === dayKey(now, timeZone)
  const options: Intl.DateTimeFormatOptions = today
    ? { hour: '2-digit', minute: '2-digit' }
    : { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
  try {
    return new Intl.DateTimeFormat(locale, { ...options, timeZone }).format(date)
  } catch {
    return new Intl.DateTimeFormat(locale, options).format(date)
  }
}

/** The text with its first letter in upper case ("yesterday" → "Yesterday", "há 3 horas" → "Há 3 horas"). */
export function capitalize(text: string, locale: string): string {
  const [first = '', ...rest] = [...text]
  return first.toLocaleUpperCase(locale) + rest.join('')
}

/** Full date and time in the user's language, optionally in a time zone. */
export function formatDateTime(value: string | Date, locale: string, timeZone?: string): string {
  const date = typeof value === 'string' ? new Date(value) : value
  try {
    return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(date)
  } catch {
    return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(date)
  }
}

/** `America/Sao_Paulo` → `America / Sao Paulo`. */
export function formatTimeZoneName(timeZone: string): string {
  return timeZone.replaceAll('_', ' ').replaceAll('/', ' / ')
}

/**
 * A time zone as people know it, in their language: "Brasilia Standard Time" / "Horário Padrão de Brasília"
 * (CLDR's generic name, or the specific one when the generic is only an offset, as for UTC). Falls back to the id.
 */
export function timeZoneDisplayName(timeZone: string, locale: string, date = new Date()): string {
  const nameIn = (style: 'longGeneric' | 'long') => {
    try {
      return new Intl.DateTimeFormat(locale, { timeZone, timeZoneName: style })
        .formatToParts(date)
        .find((part) => part.type === 'timeZoneName')?.value
    } catch {
      return undefined
    }
  }
  const isOffset = (name?: string) => !name || /^(?:GMT|UTC)(?:[+\-−]\d|$)/.test(name)
  const generic = nameIn('longGeneric')
  if (!isOffset(generic)) return generic!
  const specific = nameIn('long')
  return isOffset(specific) ? formatTimeZoneName(timeZone) : specific!
}

/** `GMT-03:00`-style offset of a time zone right now (empty when the zone is unknown). */
export function timeZoneOffset(timeZone: string, date = new Date()): string {
  try {
    const part = new Intl.DateTimeFormat('en-US', { timeZone, timeZoneName: 'longOffset' })
      .formatToParts(date)
      .find((p) => p.type === 'timeZoneName')
    return part?.value ?? ''
  } catch {
    return ''
  }
}

/** Two-letter initials of a display name ("Ana Maria Souza" → "AS"). */
export function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'
  const first = [...words[0]!][0] ?? ''
  const last = words.length > 1 ? ([...words[words.length - 1]!][0] ?? '') : ''
  return (first + last).toLocaleUpperCase()
}

/** A stable small number for a string, used to pick a room color for an avatar. */
export function hashOf(value: string): number {
  let hash = 0
  for (const char of value) hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  return hash
}
