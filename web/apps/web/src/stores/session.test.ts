import type { MeDto } from '@controlhome/api-client'
import { THEME_STORAGE_KEY, useTheme } from '@controlhome/ui'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { ApiError } from '../lib/problem'
import { useSessionStore } from './session'

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
  completeSetup: vi.fn(),
  configureDatabase: vi.fn(),
  updateProfile: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const ana: MeDto = {
  id: '6a0b7c1e-0000-4000-8000-000000000001',
  userName: 'ana',
  displayName: 'Ana Souza',
  roles: ['Admin'],
  locale: 'pt-BR',
  theme: 'dark',
}

let i18n: ReturnType<typeof createAppI18n>

beforeEach(() => {
  vi.resetAllMocks()
  setActivePinia(createPinia())
  i18n = createAppI18n({ locale: 'en' })
  useTheme().setMode('system')
})

describe('session store', () => {
  it('signs in and applies the saved language and theme', async () => {
    api.login.mockResolvedValue({ data: ana, error: undefined })
    const session = useSessionStore()

    await session.login({ userName: 'ana', password: 'correct horse', rememberMe: true })

    expect(api.login).toHaveBeenCalledWith({
      body: { userName: 'ana', password: 'correct horse', rememberMe: true },
    })
    expect(session.me).toEqual(ana)
    expect(session.isAuthenticated).toBe(true)
    expect(i18n.global.locale.value).toBe('pt-BR')
    expect(useTheme().mode.value).toBe('dark')
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('throws the problem when the sign-in is refused', async () => {
    api.login.mockResolvedValue({
      data: undefined,
      error: { status: 401, code: 'auth.invalid_credentials' },
      response: new Response(null, { status: 401 }),
    })
    const session = useSessionStore()

    const failure = session.login({ userName: 'ana', password: 'wrong', rememberMe: false })

    await expect(failure).rejects.toBeInstanceOf(ApiError)
    await expect(failure).rejects.toMatchObject({
      problem: { code: 'auth.invalid_credentials' },
      status: 401,
    })
    expect(session.me).toBeNull()
  })

  it('signs out and forgets the user, keeping the local preferences', async () => {
    api.login.mockResolvedValue({ data: ana, error: undefined })
    api.logout.mockResolvedValue({ data: {}, error: undefined })
    const session = useSessionStore()
    await session.login({ userName: 'ana', password: 'correct horse', rememberMe: true })

    await session.logout()

    expect(api.logout).toHaveBeenCalledTimes(1)
    expect(session.me).toBeNull()
    expect(session.isAuthenticated).toBe(false)
    expect(i18n.global.locale.value).toBe('pt-BR')
  })

  it('forgets the user even when the sign-out request fails', async () => {
    api.logout.mockRejectedValue(new TypeError('Failed to fetch'))
    const session = useSessionStore()
    session.$patch({ me: ana })

    await expect(session.logout()).rejects.toThrow()
    expect(session.me).toBeNull()
  })

  it('checks roles', () => {
    const session = useSessionStore()
    expect(session.hasRole('Admin')).toBe(false)

    session.$patch({ me: { ...ana, roles: ['Member'] } })
    expect(session.hasRole('Admin')).toBe(false)
    expect(session.hasRole('Admin', 'Member')).toBe(true)
    expect(session.isAdmin).toBe(false)

    session.$patch({ me: ana })
    expect(session.isAdmin).toBe(true)
  })

  it('probes the session only once', async () => {
    api.getMe.mockResolvedValue({ data: undefined, error: { code: 'auth.required' } })
    const session = useSessionStore()

    await session.ensureMe()
    await session.ensureMe()

    expect(api.getMe).toHaveBeenCalledTimes(1)
    expect(session.me).toBeNull()
    expect(session.meLoaded).toBe(true)
  })

  it('keeps the last known status when the server cannot be reached', async () => {
    const status = {
      setupCompleted: true,
      databaseState: 'Ready',
      databaseConfigured: true,
      databaseSource: 'environment',
      databaseError: null,
      version: '0.3.0',
    }
    api.getSetupStatus.mockResolvedValueOnce({ data: status, error: undefined })
    api.getSetupStatus.mockResolvedValueOnce({ data: undefined, error: new TypeError('Failed to fetch') })
    const session = useSessionStore()

    await session.loadStatus()
    await session.loadStatus()

    expect(session.status).toEqual(status)
  })

  it('saves the database connection and takes the status the server answers with', async () => {
    const accepted = {
      setupCompleted: false,
      databaseState: 'Connecting',
      databaseConfigured: true,
      databaseSource: 'file',
      databaseError: null,
      version: '0.4.0',
    }
    api.configureDatabase.mockResolvedValue({ data: accepted, error: undefined })
    const session = useSessionStore()
    const request = {
      setupCode: 'ABCD',
      host: '192.168.1.20',
      port: 1433,
      database: 'ControlHome',
      userName: 'sa',
      password: 'secret',
      trustServerCertificate: true,
    }

    await expect(session.configureDatabase(request)).resolves.toEqual(accepted)

    expect(api.configureDatabase).toHaveBeenCalledWith({ body: request })
    expect(session.status).toEqual(accepted)
  })

  it('throws the problem when the database connection is refused', async () => {
    api.configureDatabase.mockResolvedValue({
      data: undefined,
      error: { status: 409, code: 'db.create_denied', script: 'CREATE DATABASE [ControlHome];' },
      response: new Response(null, { status: 409 }),
    })
    const session = useSessionStore()

    const error = await session
      .configureDatabase({
        setupCode: 'ABCD',
        host: 'db',
        port: 1433,
        database: 'ControlHome',
        userName: 'app',
        password: 'secret',
        trustServerCertificate: true,
      })
      .catch((reason: unknown) => reason)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).problem).toMatchObject({
      code: 'db.create_denied',
      script: 'CREATE DATABASE [ControlHome];',
    })
    expect(session.status).toBeNull()
  })

  it('saves top-bar preferences to the profile', async () => {
    api.updateProfile.mockImplementation(async ({ body }) => ({
      data: { ...ana, ...body },
      error: undefined,
    }))
    const session = useSessionStore()
    session.$patch({ me: ana })

    await session.savePreferences({ theme: 'light' })

    expect(api.updateProfile).toHaveBeenCalledWith({
      body: { displayName: 'Ana Souza', locale: 'pt-BR', theme: 'light' },
    })
    expect(session.me?.theme).toBe('light')
    expect(useTheme().mode.value).toBe('light')
  })

  it('completes setup, applies the home language and keeps the theme chosen during setup', async () => {
    const admin = { ...ana, locale: 'en', theme: 'system' }
    api.completeSetup.mockResolvedValue({ data: admin, error: undefined })
    api.updateProfile.mockImplementation(async ({ body }) => ({
      data: { ...admin, ...body },
      error: undefined,
    }))
    useTheme().setMode('dark')
    const session = useSessionStore()
    session.$patch({
      status: {
        setupCompleted: false,
        databaseState: 'Ready',
        databaseConfigured: true,
        databaseSource: 'environment',
        databaseError: null,
        version: '0.3.0',
      },
    })

    const me = await session.completeSetup({
      setupCode: 'ABCD',
      admin: { userName: 'ana', displayName: 'Ana Souza', password: 'correct horse battery' },
      home: {
        name: 'Casa',
        timeZoneId: 'America/Sao_Paulo',
        latitude: null,
        longitude: null,
        locale: 'en',
        currency: 'BRL',
        pricePerKwh: null,
      },
    })

    expect(session.status?.setupCompleted).toBe(true)
    expect(api.updateProfile).toHaveBeenCalledWith({
      body: { displayName: 'Ana Souza', locale: 'en', theme: 'dark' },
    })
    expect(me.theme).toBe('dark')
    expect(useTheme().mode.value).toBe('dark')
    expect(i18n.global.locale.value).toBe('en')
  })
})
