import { describe, expect, it } from 'vitest'
import {
  capitalize,
  dayPartOf,
  formatClock,
  formatRelative,
  formatTimeZoneName,
  hourIn,
  initialsOf,
  timeZoneDisplayName,
  timeZoneOffset,
} from './format'

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

  it('uses the given text under a minute', () => {
    expect(formatRelative('2026-10-12T11:59:40Z', now, 'pt-BR', 'Agora mesmo')).toBe('Agora mesmo')
    expect(formatRelative('2026-10-12T09:00:00Z', now, 'en', 'Just now')).toBe('3 hours ago')
  })

  it('never points into the future: a time ahead of now (clocks apart) is just now', () => {
    expect(formatRelative('2026-10-12T16:00:00Z', now, 'en', 'Just now')).toBe('Just now')
    expect(formatRelative('2026-10-12T19:00:00Z', now, 'pt-BR')).toBe('agora')
  })
})

describe('formatClock', () => {
  const now = new Date('2026-10-12T12:00:00Z')

  it('is the time of day today, and the date with it on other days (in the home time zone)', () => {
    expect(formatClock('2026-10-12T09:40:00Z', now, 'pt-BR', 'America/Sao_Paulo')).toBe('06:40')
    expect(formatClock('2026-10-11T09:40:00Z', now, 'pt-BR', 'America/Sao_Paulo')).toMatch(
      /^11 de out\.?,? 06:40$/,
    )
    expect(formatClock('2026-10-12T09:40:00Z', now, 'en', 'America/Sao_Paulo')).toBe('06:40 AM')
  })

  it('takes the browser zone for an unknown id', () => {
    expect(() => formatClock('2026-10-12T09:40:00Z', now, 'en', 'Not/AZone')).not.toThrow()
  })
})

describe('capitalize', () => {
  it.each([
    ['yesterday', 'en', 'Yesterday'],
    ['há 3 horas', 'pt-BR', 'Há 3 horas'],
    ['3 hours ago', 'en', '3 hours ago'],
    ['', 'en', ''],
  ])('%s → %s', (text, locale, expected) => {
    expect(capitalize(text, locale)).toBe(expected)
  })
})

describe('time zone labels', () => {
  const instant = new Date('2026-10-12T12:00:00Z')

  it('formats names and offsets', () => {
    expect(formatTimeZoneName('America/Sao_Paulo')).toBe('America / Sao Paulo')
    expect(timeZoneOffset('America/Sao_Paulo', instant)).toBe('GMT-03:00')
    expect(timeZoneOffset('Not/AZone')).toBe('')
  })

  it('names a zone the way people know it, in their language', () => {
    expect(timeZoneDisplayName('America/Sao_Paulo', 'en', instant)).toBe('Brasilia Standard Time')
    expect(timeZoneDisplayName('America/Sao_Paulo', 'pt-BR', instant)).toBe('Horário Padrão de Brasília')
    // UTC has no generic name (only "GMT+00:00"): the specific one instead.
    expect(timeZoneDisplayName('UTC', 'en', instant)).toBe('Coordinated Universal Time')
    expect(timeZoneDisplayName('Not/AZone', 'en', instant)).toBe('Not / AZone')
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
