import { describe, expect, it } from 'vitest'
import {
  createDatabaseDraft,
  createDraft,
  databaseErrorsByStep,
  databaseProgress,
  errorsByStep,
  firstStepWithErrors,
  nextStep,
  normalizeSetupCode,
  previousStep,
  setupSteps,
  toDatabaseRequest,
  toRequest,
} from './setup'

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

describe('wizard steps', () => {
  it('asks for the database (right after the code) only when the server has none', () => {
    expect(setupSteps(true)).toEqual(['code', 'database', 'admin', 'home', 'review'])
    expect(setupSteps(false)).toEqual(['code', 'admin', 'home', 'review'])
  })

  it('moves forward and back through the steps in order', () => {
    const withDatabase = setupSteps(true)
    expect(nextStep(withDatabase, 'code')).toBe('database')
    expect(nextStep(withDatabase, 'database')).toBe('admin')
    expect(previousStep(withDatabase, 'admin')).toBe('database')
    expect(previousStep(withDatabase, 'database')).toBe('code')
    expect(previousStep(withDatabase, 'code')).toBeUndefined()
    expect(nextStep(withDatabase, 'review')).toBeUndefined()

    const withoutDatabase = setupSteps(false)
    expect(nextStep(withoutDatabase, 'code')).toBe('admin')
    expect(previousStep(withoutDatabase, 'admin')).toBe('code')
  })

  it('finds the first step with errors in the order of the given steps', () => {
    expect(firstStepWithErrors({ admin: ['userName'], database: ['host'] }, setupSteps(true))).toBe(
      'database',
    )
    expect(firstStepWithErrors({ admin: ['userName'] }, setupSteps(false))).toBe('admin')
  })
})

describe('database step model', () => {
  it('suggests this server, the default port and database, and trusting the server certificate', () => {
    expect(createDatabaseDraft('192.168.1.20')).toEqual({
      host: '192.168.1.20',
      port: '1433',
      database: 'ControlHome',
      userName: '',
      password: '',
      trustServerCertificate: true,
    })
    // IPv6 hosts come with brackets from the browser; SQL Server wants the bare address.
    expect(createDatabaseDraft('[fd00::20]').host).toBe('fd00::20')
    expect(createDatabaseDraft('').host).toBe('')
    expect(createDraft('en', 'umbrel.local').database.host).toBe('umbrel.local')
  })

  it('builds the request with the setup code from the first step', () => {
    const database = {
      host: ' 192.168.1.20 ',
      port: ' 14330 ',
      database: ' Casa ',
      userName: ' sa ',
      password: ' spaces kept ',
      trustServerCertificate: false,
    }

    expect(toDatabaseRequest(' abcd-efgh jkmn ', database)).toEqual({
      setupCode: 'ABCD-EFGHJKMN',
      host: '192.168.1.20',
      port: 14330,
      database: 'Casa',
      userName: 'sa',
      password: ' spaces kept ',
      trustServerCertificate: false,
    })
  })

  it('maps the database endpoint field errors to the code and database steps', () => {
    expect(
      databaseErrorsByStep(['setupCode', 'host', 'port', 'database', 'userName', 'password', 'other']),
    ).toEqual({
      code: ['setupCode'],
      database: ['host', 'port', 'database', 'userName', 'password'],
    })
    expect(databaseErrorsByStep([])).toEqual({})
  })

  it('turns the database state into progress', () => {
    expect(databaseProgress('NotConfigured')).toBe('connecting')
    expect(databaseProgress('Connecting')).toBe('connecting')
    expect(databaseProgress('Migrating')).toBe('preparing')
    expect(databaseProgress('Ready')).toBe('ready')
    expect(databaseProgress('Failed')).toBe('failed')
    expect(databaseProgress('Something new')).toBe('connecting')
  })
})
