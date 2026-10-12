import { describe, expect, it } from 'vitest'
import { createDraft, errorsByStep, firstStepWithErrors, normalizeSetupCode, toRequest } from './setup'

describe('setup wizard model', () => {
  it('defaults to São Paulo, BRL and the UI language', () => {
    const draft = createDraft('pt-BR')
    expect(draft.home).toMatchObject({ timeZoneId: 'America/Sao_Paulo', currency: 'BRL', locale: 'pt-BR' })
  })

  it('builds the request: trimmed text, numbers parsed (comma or dot), empty optional numbers as null', () => {
    const draft = createDraft('en')
    draft.setupCode = ' abcd-efgh jkmn '
    draft.admin = {
      displayName: ' Ana ',
      userName: ' ana ',
      password: ' secret phrase ',
      confirmPassword: '',
    }
    draft.home = {
      ...draft.home,
      name: ' Casa ',
      latitude: '-23,5505',
      longitude: '-46.6333',
      pricePerKwh: '',
    }

    expect(toRequest(draft)).toEqual({
      setupCode: 'ABCD-EFGHJKMN',
      admin: { userName: 'ana', displayName: 'Ana', password: ' secret phrase ' },
      home: {
        name: 'Casa',
        timeZoneId: 'America/Sao_Paulo',
        latitude: -23.5505,
        longitude: -46.6333,
        locale: 'en',
        currency: 'BRL',
        pricePerKwh: null,
      },
    })
  })

  it('normalizes the setup code', () => {
    expect(normalizeSetupCode('ab12 cd34-ef56')).toBe('AB12CD34-EF56')
  })

  it('maps server field errors to wizard steps', () => {
    const errors = errorsByStep(['home.timeZoneId', 'admin.userName', 'setupCode', 'admin.password', ''])
    expect(errors).toEqual({
      code: ['setupCode'],
      admin: ['userName', 'password'],
      home: ['timeZoneId'],
    })
    expect(firstStepWithErrors(errors)).toBe('code')
    expect(firstStepWithErrors({ home: ['name'] })).toBe('home')
    expect(firstStepWithErrors({})).toBeUndefined()
  })
})
