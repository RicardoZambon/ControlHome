import type { ConnectorDto, FlowStepDto, MeDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../../App.vue'
import type { AppLocale } from '../../../i18n'
import { mountWithApp } from '../../../test/app'
import { CATALOGUE, INTEGRATIONS, ROOMS, basicHome } from '../../../test/devices'

// The integrations pages (list, catalogue, setup and re-authorization flows with the import review, detail) mounted in
// the real app against a mocked API.

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  getHome: vi.fn(),
  listDevices: vi.fn(),
  listCapabilities: vi.fn(),
  listRooms: vi.fn(),
  listIntegrations: vi.fn(),
  listConnectors: vi.fn(),
  startIntegrationFlow: vi.fn(),
  continueIntegrationFlow: vi.fn(),
  cancelIntegrationFlow: vi.fn(),
  startIntegrationReauthFlow: vi.fn(),
  updateIntegration: vi.fn(),
  deleteIntegration: vi.fn(),
  updateDevice: vi.fn(),
  updateEndpoint: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const ok = (data: unknown, status = 200) => ({
  data,
  error: undefined,
  response: new Response(null, { status }),
})
const text = (en: string, ptBr = en) => ({ en, ptBr })

const SIMULATED: ConnectorDto = {
  key: 'simulated',
  displayName: text('Simulated home', 'Casa simulada'),
  icon: 'flask-conical',
  category: 'virtual',
  configFields: [
    {
      key: 'preset',
      label: text('Preset', 'Conjunto'),
      schema: { type: 'enum', values: ['basic', 'demo'] },
      required: true,
      secret: false,
      advanced: false,
      help: null,
    },
    {
      key: 'seed',
      label: text('Seed', 'Semente'),
      schema: { type: 'number', min: 0, integer: true },
      required: false,
      secret: false,
      advanced: false,
      help: null,
    },
  ],
  deviceConfigFields: [],
  features: {
    setupFlow: true,
    reauth: true,
    pushInventory: true,
    permitJoin: false,
    cloud: false,
    polling: false,
  },
}

const MQTT: ConnectorDto = {
  ...SIMULATED,
  key: 'mqtt',
  displayName: text('MQTT'),
  icon: 'radio-tower',
  category: 'protocol',
  features: { ...SIMULATED.features, reauth: false, permitJoin: true },
}

function step(type: string, extra: Partial<FlowStepDto> = {}): FlowStepDto {
  return {
    flowId: 'flow-1',
    type,
    stepId: type,
    title: null,
    description: null,
    fields: [],
    values: null,
    errors: {},
    url: null,
    pasteBack: null,
    timeoutSeconds: null,
    choices: [],
    integrationId: null,
    reason: null,
    message: null,
    ...extra,
  }
}

const FORM = step('form', {
  stepId: 'settings',
  title: text('Simulated home', 'Casa simulada'),
  description: text('Pick the simulated devices.', 'Escolha os dispositivos simulados.'),
  fields: SIMULATED.configFields,
  values: { preset: 'basic', seed: 42 },
})

function signedIn(role: 'Admin' | 'Member' | 'Viewer', locale: AppLocale): void {
  const me: MeDto = { id: 'u1', userName: 'ada', displayName: 'Ada', roles: [role], locale, theme: 'light' }
  api.getMe.mockResolvedValue(ok(me))
}

async function open(path: string, locale: AppLocale = 'en') {
  const mounted = await mountWithApp(App, { path, locale })
  await flushPromises()
  await flushPromises()
  return mounted
}

beforeEach(() => {
  vi.resetAllMocks()
  api.getSetupStatus.mockResolvedValue(
    ok({
      setupCompleted: true,
      databaseState: 'Ready',
      databaseConfigured: true,
      databaseSource: 'environment',
      databaseError: null,
      version: '0.3.0',
    }),
  )
  api.getHome.mockResolvedValue(
    ok({ id: 1, name: 'Casa Azul', timeZoneId: 'America/Sao_Paulo', locale: 'en' }),
  )
  api.listDevices.mockResolvedValue(ok(basicHome()))
  api.listCapabilities.mockResolvedValue(ok(CATALOGUE))
  api.listRooms.mockResolvedValue(ok(ROOMS))
  api.listIntegrations.mockResolvedValue(ok(INTEGRATIONS))
  api.listConnectors.mockResolvedValue(ok([SIMULATED, MQTT]))
  api.cancelIntegrationFlow.mockResolvedValue(ok(undefined, 204))
})

afterEach(() => {
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('integrations page', () => {
  it('lists the integrations with their live status and devices, each opening its page', async () => {
    signedIn('Admin', 'pt-BR')
    const { wrapper } = await open('/integrations', 'pt-BR')
    expect(wrapper.find('h1').text()).toBe('Integrações')
    expect(wrapper.get('[data-slot="integration-status"]').text()).toBe('Conectada')
    expect(wrapper.get('[data-slot="device-count"]').text()).toBe('5 dispositivos')
    expect(wrapper.find('a[href="/integrations/int-1"]').exists()).toBe(true)
    expect(wrapper.find('a[href="/integrations/add"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('without integrations, points to the catalogue and the simulated home', async () => {
    api.listIntegrations.mockResolvedValue(ok([]))
    signedIn('Admin', 'en')
    const { wrapper } = await open('/integrations')
    expect(wrapper.text()).toContain('Connect your first devices')
    expect(wrapper.find('a[href="/integrations/add"]').exists()).toBe(true)
    expect(wrapper.get('a[href="/integrations/add/simulated"]').text()).toContain('Try the simulated home')
    wrapper.unmount()
  })

  it('offers to sign in again where an integration needs it', async () => {
    api.listIntegrations.mockResolvedValue(ok([{ ...INTEGRATIONS[0], status: 'NeedsReauth' }]))
    signedIn('Admin', 'en')
    const { wrapper } = await open('/integrations')
    expect(wrapper.get('a[href="/integrations/int-1/reauth"]').text()).toContain('Sign in again')
    wrapper.unmount()
  })

  it('is for Admins only', async () => {
    signedIn('Member', 'en')
    const { router } = await open('/integrations/add')
    expect(router.currentRoute.value.name).toBe('forbidden')
  })
})

describe('integration catalogue', () => {
  it('groups the connectors by kind, each starting its setup', async () => {
    signedIn('Admin', 'pt-BR')
    const { wrapper } = await open('/integrations/add', 'pt-BR')
    const headings = wrapper.findAll('h2.eyebrow').map((heading) => heading.text())
    expect(headings).toEqual(['Protocolos da sua rede', 'Virtuais e demonstração'])
    expect(wrapper.get('a[href="/integrations/add/simulated"]').text()).toBe('Casa simulada')
    expect(wrapper.get('[data-connector="simulated"]').text()).toContain('Configuração guiada')
    wrapper.unmount()
  })
})

describe('integration setup', () => {
  it('runs the flow (form, then the wait on the device), then reviews the devices', async () => {
    signedIn('Admin', 'en')
    api.startIntegrationFlow.mockResolvedValue(ok(FORM))
    const { wrapper, router } = await open('/integrations/add/simulated')
    expect(api.startIntegrationFlow).toHaveBeenCalledWith({ body: { connectorKey: 'simulated' } })
    expect(wrapper.find('h1').text()).toBe('Simulated home')
    expect(wrapper.get('[data-slot="flow-wizard"]').attributes('data-step')).toBe('form')
    expect(wrapper.text()).toContain('Pick the simulated devices.')

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] })
    api.continueIntegrationFlow.mockResolvedValueOnce(
      ok(
        step('progress', {
          stepId: 'pairing',
          title: text('Press the button on the hub'),
          timeoutSeconds: 60,
        }),
      ),
    )
    await wrapper.get('form[data-slot="schema-form"]').trigger('submit')
    await flushPromises()
    expect(api.continueIntegrationFlow).toHaveBeenLastCalledWith({
      path: { flowId: 'flow-1' },
      body: { input: { preset: 'basic', seed: 42 } },
    })
    expect(wrapper.get('[data-slot="flow-wizard"]').attributes('data-step')).toBe('progress')
    expect(wrapper.text()).toContain('Press the button on the hub')
    expect(wrapper.text()).toMatch(/[0-9]+ s left/)

    // Polled with {} until it is done.
    api.continueIntegrationFlow.mockResolvedValueOnce(ok(step('done', { integrationId: 'int-1' })))
    await vi.advanceTimersByTimeAsync(1_000)
    await flushPromises()
    expect(api.continueIntegrationFlow).toHaveBeenLastCalledWith({
      path: { flowId: 'flow-1' },
      body: { input: {} },
    })
    expect(wrapper.text()).toContain('Connected')

    await vi.advanceTimersByTimeAsync(800)
    await flushPromises()
    expect(api.listDevices).toHaveBeenCalledWith({ query: { integrationId: 'int-1' } })
    const review = wrapper.get('[data-slot="import-review"]')
    expect(review.text()).toContain('5 devices found')

    // Hide the lock; the rest keep their rooms.
    api.updateEndpoint.mockResolvedValue(ok({}))
    const lock = review.get('[data-device-id="d-lock"]')
    await lock.get('button[role="checkbox"]').trigger('click')
    await review.get('[data-slot="review-finish"]').trigger('click')
    await flushPromises()
    expect(api.updateEndpoint).toHaveBeenCalledWith({
      path: { id: 'e-lock' },
      body: { name: null, icon: null, roomId: null, hidden: true },
    })
    expect(api.updateDevice).not.toHaveBeenCalled()
    await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe('/integrations/int-1'))
    wrapper.unmount()
  })

  it('shows the server’s field errors and the reason of an aborted flow', async () => {
    signedIn('Admin', 'pt-BR')
    api.startIntegrationFlow.mockResolvedValue(ok(FORM))
    const { wrapper } = await open('/integrations/add/simulated', 'pt-BR')
    api.continueIntegrationFlow.mockResolvedValueOnce(
      ok({
        ...FORM,
        values: { preset: 'demo', seed: 7 },
        errors: { seed: text('Not valid.', 'Não é válido.') },
      }),
    )
    await wrapper.get('form[data-slot="schema-form"]').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Não é válido.')

    api.continueIntegrationFlow.mockResolvedValueOnce(
      ok(step('abort', { reason: 'timeout', message: text('Too late.', 'Tarde demais.') })),
    )
    await wrapper.get('form[data-slot="schema-form"]').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[data-slot="flow-abort"]').text()).toContain('Tarde demais.')
    wrapper.unmount()
  })

  it('signs an integration in again (or changes its settings) and goes back to it', async () => {
    signedIn('Admin', 'en')
    api.startIntegrationReauthFlow.mockResolvedValue(ok(FORM))
    const { wrapper, router } = await open('/integrations/int-1/reauth')
    expect(api.startIntegrationReauthFlow).toHaveBeenCalledWith({ path: { id: 'int-1' } })
    expect(wrapper.find('h1').text()).toBe('Simulated home')
    // A simulated hub has no sign-in: the page says what it does instead.
    expect(wrapper.find('header .eyebrow').text()).toBe('Change settings')
    api.continueIntegrationFlow.mockResolvedValueOnce(ok(step('done', { integrationId: 'int-1' })))
    await wrapper.get('form[data-slot="schema-form"]').trigger('submit')
    await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe('/integrations/int-1'))
    expect(document.body.textContent).toContain('Simulated home is updated.')
    wrapper.unmount()
  })

  it('cancels the flow on the server when leaving half-way', async () => {
    signedIn('Admin', 'en')
    api.startIntegrationFlow.mockResolvedValue(ok(FORM))
    const { wrapper, router } = await open('/integrations/add/simulated')
    const cancel = wrapper.findAll('button').find((button) => button.text() === 'Cancel')!
    await cancel.trigger('click')
    await flushPromises()
    expect(api.cancelIntegrationFlow).toHaveBeenCalledWith({ path: { flowId: 'flow-1' } })
    expect(router.currentRoute.value.name).toBe('integration-catalog')
    wrapper.unmount()
  })
})

describe('integration page', () => {
  it('shows the status and the devices, turns it off, and removes it after confirming', async () => {
    signedIn('Admin', 'en')
    const { wrapper, router } = await open('/integrations/int-1')
    expect(wrapper.find('h1').text()).toBe('Simulated home')
    expect(wrapper.get('[data-slot="integration-status"]').text()).toBe('Connected')
    expect(wrapper.findAll('[data-slot="integration-devices"] li')).toHaveLength(5)

    api.updateIntegration.mockResolvedValue(ok({ ...INTEGRATIONS[0], enabled: false, status: 'Stopped' }))
    await wrapper.get('[data-slot="integration-enabled"]').trigger('click')
    await flushPromises()
    expect(api.updateIntegration).toHaveBeenCalledWith({
      path: { id: 'int-1' },
      body: { name: 'Simulated home', enabled: false },
    })
    expect(wrapper.get('[data-slot="integration-status"]').text()).toBe('Stopped')

    api.deleteIntegration.mockResolvedValue(ok(undefined, 204))
    await wrapper.get('[data-slot="integration-remove"]').trigger('click')
    await flushPromises()
    const confirm = [...document.querySelectorAll('button')].find(
      (button) =>
        button.textContent?.trim() === 'Remove integration' && button.closest('[role="alertdialog"]'),
    )!
    confirm.click()
    await flushPromises()
    expect(api.deleteIntegration).toHaveBeenCalledWith({ path: { id: 'int-1' } })
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('integrations'))
    wrapper.unmount()
  })

  it('asks to sign in again when the integration needs it', async () => {
    api.listIntegrations.mockResolvedValue(
      ok([{ ...INTEGRATIONS[0], status: 'NeedsReauth', statusMessage: 'The hub rejected its token.' }]),
    )
    signedIn('Admin', 'en')
    const { wrapper } = await open('/integrations/int-1')
    const callout = wrapper.get('[data-slot="integration-attention"]')
    expect(callout.text()).toContain('The hub rejected its token.')
    expect(callout.get('a[href="/integrations/int-1/reauth"]').text()).toContain('Sign in again')
    wrapper.unmount()
  })

  it('says when the integration is gone', async () => {
    signedIn('Admin', 'en')
    const { wrapper } = await open('/integrations/nope')
    expect(wrapper.find('h1').text()).toBe('Integration not found')
    wrapper.unmount()
  })
})
