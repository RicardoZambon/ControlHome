import { describe, expect, it } from 'vitest'
import { createAppI18n } from '../../i18n'
import { pendingLabel } from './pendingLabel'

function labels(locale: 'en' | 'pt-BR') {
  const i18n = createAppI18n({ locale })
  const t = (key: string, named?: Record<string, unknown>) => i18n.global.t(key, named ?? {})
  const te = (key: string) => i18n.global.te(key)
  return (command: string, args?: Record<string, unknown>, awaitingPin?: boolean) =>
    pendingLabel({ command, args, awaitingPin }, t, te, locale)
}

describe('pendingLabel', () => {
  it('says what the command is doing, in its own words', () => {
    const label = labels('en')
    expect(label('turnOn')).toBe('Turning on…')
    expect(label('turnOff')).toBe('Turning off…')
    expect(label('lock')).toBe('Locking…')
    expect(label('unlock')).toBe('Unlocking…')
    expect(label('setLevel', { level: 40 })).toBe('Setting 40%…')
    expect(label('setTargetTemperature', { targetTemperature: 22.5 })).toBe('Setting 22.5 °C…')
    expect(label('setMode', { mode: 'cool' })).toBe('Switching to Cool…')
    expect(label('somethingElse')).toBe('Updating…')
  })

  it('a safety-critical command waiting for the PIN has done nothing yet', () => {
    expect(labels('en')('unlock', undefined, true)).toBe('Waiting for your PIN')
    expect(labels('pt-BR')('unlock', undefined, true)).toBe('Aguardando seu PIN')
  })

  it('speaks Portuguese', () => {
    const label = labels('pt-BR')
    expect(label('turnOn')).toBe('Ligando…')
    expect(label('setTargetTemperature', { targetTemperature: 22.5 })).toBe('Ajustando para 22,5 °C…')
    expect(label('setMode', { mode: 'heat' })).toBe('Mudando para Aquecer…')
  })
})
