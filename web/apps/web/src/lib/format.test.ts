import { describe, expect, it } from 'vitest'
import { dayPartOf, formatRelative, formatTimeZoneName, hourIn, initialsOf, timeZoneOffset } from './format'

describe('dayPartOf', () => {
  // 2026-10-12T12:30Z is 09:30 in São Paulo (UTC-3) and 21:30 in Tokyo (UTC+9).
  const instant = new Date('2026-10-12T12:30:00Z')

  it('uses the home time zone', () => {
    expect(hourIn(instant, 'America/Sao_Paulo')).toBe(9)
    expect(dayPartOf(instant, 'America/Sao_Paulo')).toBe('morning')
    expect(dayPartOf(instant, 'Asia/Tokyo')).toBe('evening')
    expect(dayPartOf(instant, 'UTC')).toBe('afternoon')
    expect(dayPartOf(new Date('2026-10-12T03:00:00Z'), 'UTC')).toBe('night')
  })

  it('falls back to the browser zone for an unknown id', () => {
    expect(() => dayPartOf(instant, 'Not/AZone')).not.toThrow()
  })
})

describe('formatRelative', () => {
  const now = new Date('2026-10-12T12:00:00Z')

  it.each([
    ['2026-10-12T11:59:40Z', 'en', 'now'],
    ['2026-10-12T09:00:00Z', 'en', '3 hours ago'],
    ['2026-10-11T12:00:00Z', 'en', 'yesterday'],
    ['2026-10-12T09:00:00Z', 'pt-BR', 'há 3 horas'],
  ])('%s in %s → %s', (value, locale, expected) => {
    expect(formatRelative(value, now, locale)).toBe(expected)
  })
})

describe('time zone labels', () => {
  it('formats names and offsets', () => {
    expect(formatTimeZoneName('America/Sao_Paulo')).toBe('America / Sao Paulo')
    expect(timeZoneOffset('America/Sao_Paulo', new Date('2026-10-12T12:00:00Z'))).toBe('GMT-03:00')
    expect(timeZoneOffset('Not/AZone')).toBe('')
  })
})

describe('initialsOf', () => {
  it.each([
    ['Ana Maria Souza', 'AS'],
    ['ana', 'A'],
    ['  Émile   zola ', 'ÉZ'],
    ['', '?'],
  ])('%s → %s', (name, initials) => {
    expect(initialsOf(name)).toBe(initials)
  })
})
