import { flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App.vue'
import { mountWithApp } from './test/app'

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  login: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const status = (setupCompleted: boolean) => ({
  data: { setupCompleted, databaseState: 'Ready', databaseConfigured: true, version: '0.3.0+abc' },
  error: undefined,
})

beforeEach(() => {
  vi.resetAllMocks()
  api.getMe.mockResolvedValue({ data: undefined, error: { code: 'auth.required' } })
})

describe('App', () => {
  it('sends a visitor to sign in once setup is done', async () => {
    api.getSetupStatus.mockResolvedValue(status(true))
    const { wrapper, router } = await mountWithApp(App, { path: '/users' })
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/users')
    expect(wrapper.text()).toContain('ControlHome')
    expect(wrapper.find('h1').text()).toBe('Welcome back')
    expect(wrapper.find('[data-slot="button"][type="submit"]').exists()).toBe(true)
    expect(document.title).toBe('Sign in · ControlHome')
    wrapper.unmount()
  })

  it('opens the setup wizard before setup is done', async () => {
    api.getSetupStatus.mockResolvedValue(status(false))
    const { wrapper, router } = await mountWithApp(App, { path: '/' })
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('setup')
    expect(api.getMe).not.toHaveBeenCalled()
    expect(wrapper.find('h1').text()).toBe('Welcome home')
    wrapper.unmount()
  })

  it('explains the database variable when no database is configured', async () => {
    api.getSetupStatus.mockResolvedValue({
      data: {
        setupCompleted: false,
        databaseState: 'NotConfigured',
        databaseConfigured: false,
        version: '0.3.0',
      },
      error: undefined,
    })
    const { wrapper } = await mountWithApp(App, { path: '/setup', locale: 'pt-BR' })
    await flushPromises()

    expect(wrapper.text()).toContain('CONTROLHOME_DB_CONNECTION')
    expect(wrapper.text()).toContain('Conecte um banco de dados')
    wrapper.unmount()
  })

  it('shows the translated reason when the sign-in is refused', async () => {
    api.getSetupStatus.mockResolvedValue(status(true))
    api.login.mockResolvedValue({
      data: undefined,
      error: { status: 401, code: 'auth.invalid_credentials' },
      response: new Response(null, { status: 401 }),
    })
    const { wrapper } = await mountWithApp(App, { path: '/login', locale: 'pt-BR' })
    await flushPromises()

    await wrapper.find('input[autocomplete="username"]').setValue('ana')
    await wrapper.find('input[autocomplete="current-password"]').setValue('wrong password')
    await wrapper.find('form').trigger('submit')

    // vee-validate validates asynchronously before calling the submit handler.
    await vi.waitFor(() => expect(wrapper.find('[role="alert"]').exists()).toBe(true))
    expect(api.login).toHaveBeenCalledWith({
      body: { userName: 'ana', password: 'wrong password', rememberMe: true },
    })
    expect(wrapper.find('[role="alert"]').text()).toContain('Usuário ou senha incorretos.')
    wrapper.unmount()
  })
})
