import type { DeviceDetailDto, DeviceSummaryDto, MeDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../../App.vue'
import type { AppLocale } from '../../../i18n'
import { mountWithApp } from '../../../test/app'
import { CATALOGUE, INTEGRATIONS, OBSERVED, ROOMS, basicHome } from '../../../test/devices'

// The device pages and the integrations page mounted in the real app (router, guards, i18n that throws on a missing
// key), in both languages and for each role, against a mocked API.

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  getHome: vi.fn(),
  listDevices: vi.fn(),
  listCapabilities: vi.fn(),
  listRooms: vi.fn(),
  listIntegrations: vi.fn(),
  listConnectors: vi.fn(),
  getDevice: vi.fn(),
  sendCommand: vi.fn(),
  setEndpointFavorite: vi.fn(),
  createIntegration: vi.fn(),
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

function detail(device: DeviceSummaryDto): DeviceDetailDto {
  return {
    ...device,
    integrationName: 'Simulated home',
    connectorKey: 'simulated',
    externalId: device.id.replace('d-', '') + '-1',
    manufacturer: 'ControlHome',
    model: 'Simulated',
    firmware: '1.0.0',
    connections: { mac: '02:00:00:00:2A:01' },
    createdAtUtc: OBSERVED,
    children: [],
  }
}

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
    ok({
      id: 1,
      name: 'Casa Azul',
      timeZoneId: 'America/Sao_Paulo',
      latitude: null,
      longitude: null,
      locale: 'en',
      currency: 'BRL',
      pricePerKwh: null,
    }),
  )
  api.listDevices.mockResolvedValue(ok(basicHome()))
  api.listCapabilities.mockResolvedValue(ok(CATALOGUE))
  api.listRooms.mockResolvedValue(ok(ROOMS))
  api.listIntegrations.mockResolvedValue(ok(INTEGRATIONS))
  api.getDevice.mockImplementation(({ path }: { path: { id: string } }) =>
    Promise.resolve(ok(detail(basicHome().find((device) => device.id === path.id)!))),
  )
})

afterEach(() => {
  document.body.innerHTML = ''
})

describe.each(['en', 'pt-BR'] as const)('devices page (%s)', (locale) => {
  it('shows every device as a tile, grouped by room, with its state', async () => {
    signedIn('Admin', locale)
    const { wrapper } = await open('/devices', locale)
    expect(wrapper.find('h1').text()).toBe(locale === 'en' ? 'Devices' : 'Dispositivos')
    const groups = wrapper.findAll('[data-slot="room-group"] h2').map((heading) => heading.text())
    expect(groups).toEqual(['Living room', 'Kitchen', 'Bedroom', locale === 'en' ? 'No room' : 'Sem cômodo'])
    const tiles = wrapper.findAll('[data-slot="device-tile"]')
    expect(tiles).toHaveLength(5)
    const states = tiles.map((tile) => tile.get('[data-slot="tile-state"]').text())
    expect(states[0]).toContain(locale === 'en' ? 'On' : 'Ligado')
    expect(states[0]).toContain('80 %')
    expect(states[2]).toContain(locale === 'en' ? 'Cooling to 22 °C' : 'Resfriando até 22 °C')
    expect(states[4]).toContain(locale === 'en' ? 'Locked' : 'Trancada')
    expect(tiles[0]!.attributes('data-tone')).toBe('on')
    // Each tile opens its device.
    expect(tiles[0]!.get('a').attributes('href')).toBe('/devices/d-light')
    wrapper.unmount()
  })
})

describe('devices page', () => {
  it('gives Members and Admins quick actions, Viewers only the states', async () => {
    signedIn('Member', 'en')
    const member = await open('/devices')
    expect(member.wrapper.findAll('[data-slot="device-tile"] [role="switch"]')).toHaveLength(3)
    expect(member.wrapper.find('[data-slot="lock-quick"]').exists()).toBe(true)
    // Not an Admin: no way to the integrations.
    expect(member.wrapper.find('a[href="/integrations"]').exists()).toBe(false)
    member.wrapper.unmount()

    signedIn('Viewer', 'en')
    const viewer = await open('/devices')
    expect(viewer.wrapper.findAll('[data-slot="device-tile"]')).toHaveLength(5)
    expect(viewer.wrapper.findAll('[data-slot="device-tile"] button')).toHaveLength(0)
    viewer.wrapper.unmount()
  })

  it('switches a light from its tile, optimistically', async () => {
    signedIn('Member', 'en')
    api.sendCommand.mockResolvedValue(ok({ commandId: 'c1' }, 202))
    const { wrapper } = await open('/devices')
    const power = wrapper.get('[data-slot="device-tile"] [role="switch"]')
    expect(power.attributes('aria-label')).toBe('Power, Living room light')
    expect(power.attributes('aria-checked')).toBe('true')
    await power.trigger('click')
    expect(power.attributes('aria-checked')).toBe('false')
    await flushPromises()
    expect(api.sendCommand).toHaveBeenCalledWith({
      path: { id: 'e-light' },
      body: expect.objectContaining({ capabilityId: 'switch', command: 'turnOff', arguments: null }),
    })
    wrapper.unmount()
  })

  it('filters by room through the address', async () => {
    signedIn('Admin', 'en')
    const { wrapper } = await open('/devices?room=12')
    expect(wrapper.findAll('[data-slot="device-tile"]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Bedroom air conditioner')
    expect(wrapper.text()).toContain('Clear filters')
    wrapper.unmount()
  })

  it('without devices, sends Admins to the integrations', async () => {
    api.listDevices.mockResolvedValue(ok([]))
    signedIn('Admin', 'en')
    const admin = await open('/devices')
    expect(admin.wrapper.get('#devices-empty').text()).toBe('No devices yet')
    expect(admin.wrapper.get('section[aria-labelledby="devices-empty"] a').attributes('href')).toBe(
      '/integrations',
    )
    admin.wrapper.unmount()

    signedIn('Member', 'en')
    const member = await open('/devices')
    expect(member.wrapper.text()).toContain('which an administrator sets up')
    expect(member.wrapper.find('a[href="/integrations"]').exists()).toBe(false)
    member.wrapper.unmount()
  })

  it('is in everyone’s navigation; Integrations only in the Admins’', async () => {
    signedIn('Member', 'en')
    const member = await open('/devices')
    const nav = member.wrapper.get('nav[aria-label="Main navigation"]')
    expect(nav.find('a[href="/devices"]').exists()).toBe(true)
    expect(nav.find('a[href="/integrations"]').exists()).toBe(false)
    member.wrapper.unmount()

    signedIn('Admin', 'en')
    const admin = await open('/devices')
    expect(
      admin.wrapper.get('nav[aria-label="Main navigation"]').find('a[href="/integrations"]').exists(),
    ).toBe(true)
    // The phones' tab bar keeps the main pages only.
    const tabs = admin.wrapper.get('nav[aria-label="Sections"]')
    expect(tabs.find('a[href="/devices"]').exists()).toBe(true)
    expect(tabs.find('a[href="/integrations"]').exists()).toBe(false)
    admin.wrapper.unmount()
  })
})

describe.each(['en', 'pt-BR'] as const)('device page (%s)', (locale) => {
  it('shows the controls of each capability, the readings and the attributes', async () => {
    signedIn('Admin', locale)
    const { wrapper } = await open('/devices/d-ac', locale)
    expect(wrapper.find('h1').text()).toBe('Bedroom air conditioner')
    expect(wrapper.get('[data-availability]').attributes('data-availability')).toBe('online')
    expect(wrapper.find('[data-capability="switch"] [role="switch"]').exists()).toBe(true)
    const dial = wrapper.get('[data-capability="thermostat"] [role="slider"]')
    expect(dial.attributes('aria-valuenow')).toBe('22')
    expect(dial.attributes('aria-valuemin')).toBe('16')
    expect(dial.attributes('aria-valuemax')).toBe('30')
    // The constrained modes only (no "dry").
    expect(wrapper.findAll('[data-capability="thermostat"] [data-slot="toggle-group-item"]')).toHaveLength(5)
    expect(wrapper.findAll('[data-slot="reading"]')).toHaveLength(1)
    expect(wrapper.findAll('[data-slot="attributes-table"] tbody tr')).toHaveLength(6)
    // Breadcrumb: home › Devices › device.
    expect(wrapper.get('[data-slot="breadcrumb"]').find('a[href="/devices"]').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('device page', () => {
  it('Admins edit the device; Members mark favorites', async () => {
    signedIn('Admin', 'en')
    const admin = await open('/devices/d-light')
    const buttons = admin.wrapper.findAll('header button').map((button) => button.text())
    expect(buttons).toEqual(expect.arrayContaining(['Add to favorites', 'Edit']))
    admin.wrapper.unmount()

    signedIn('Member', 'en')
    const light = basicHome()[0]!
    api.setEndpointFavorite.mockResolvedValue(ok({ ...light.endpoints[0]!, favorite: true }))
    const member = await open('/devices/d-light')
    const favorite = member.wrapper
      .findAll('header button')
      .find((button) => button.text() === 'Add to favorites')!
    expect(member.wrapper.findAll('header button').some((button) => button.text() === 'Edit')).toBe(false)
    await favorite.trigger('click')
    await flushPromises()
    expect(api.setEndpointFavorite).toHaveBeenCalledWith({
      path: { id: 'e-light' },
      body: { favorite: true },
    })
    expect(member.wrapper.findAll('header button').some((button) => button.text() === 'Favorite')).toBe(true)
    member.wrapper.unmount()
  })

  it('lets Members lock and unlock; a Viewer sees the state only', async () => {
    signedIn('Member', 'en')
    const member = await open('/devices/d-lock')
    expect(member.wrapper.get('[data-slot="lock-state"]').text()).toBe('Locked')
    const actions = member.wrapper.findAll('[data-capability="lock"] button').map((button) => button.text())
    expect(actions).toEqual(['Lock', 'Unlock'])
    member.wrapper.unmount()

    signedIn('Viewer', 'en')
    const viewer = await open('/devices/d-lock')
    expect(viewer.wrapper.get('[data-slot="lock-state"]').text()).toBe('Locked')
    expect(viewer.wrapper.findAll('[data-capability="lock"] button')).toHaveLength(0)
    expect(viewer.wrapper.text()).not.toContain('Unlock')
    viewer.wrapper.unmount()
  })

  it('says when a device is gone', async () => {
    signedIn('Admin', 'en')
    api.getDevice.mockResolvedValue({
      data: undefined,
      error: { status: 404, code: 'devices.not_found' },
      response: new Response(null, { status: 404 }),
    })
    const { wrapper } = await open('/devices/nope')
    expect(wrapper.find('h1').text()).toBe('Device not found')
    wrapper.unmount()
  })
})

describe('integrations page', () => {
  beforeEach(() => {
    api.listConnectors.mockResolvedValue(
      ok([
        {
          key: 'simulated',
          displayName: { en: 'Simulated home', ptBr: 'Casa simulada' },
          icon: 'flask-conical',
          category: 'virtual',
          configFields: [],
          deviceConfigFields: [],
          features: {
            setupFlow: false,
            reauth: false,
            pushInventory: true,
            permitJoin: false,
            cloud: false,
            polling: false,
          },
        },
      ]),
    )
  })

  it('lists the integrations with their status and devices', async () => {
    signedIn('Admin', 'pt-BR')
    const { wrapper } = await open('/integrations', 'pt-BR')
    expect(wrapper.find('h1').text()).toBe('Integrações')
    expect(wrapper.get('[data-slot="integration-status"]').text()).toBe('Conectada')
    expect(wrapper.get('a[href="/devices?integration=int-1"]').text()).toContain('5 dispositivos')
    wrapper.unmount()
  })

  it('adds the simulated demo home', async () => {
    api.listIntegrations.mockResolvedValue(ok([]))
    api.createIntegration.mockResolvedValue(ok(INTEGRATIONS[0], 201))
    signedIn('Admin', 'en')
    const { wrapper } = await open('/integrations')
    const add = wrapper.findAll('button').find((button) => button.text() === 'Add simulated demo')!
    await add.trigger('click')
    await flushPromises()
    expect(api.createIntegration).toHaveBeenCalledWith({
      body: { connectorKey: 'simulated', name: 'Simulated home', config: { preset: 'basic' } },
    })
    expect(wrapper.findAll('[data-integration-id]')).toHaveLength(1)
    wrapper.unmount()
  })

  it('is for Admins only', async () => {
    signedIn('Member', 'en')
    const { router } = await open('/integrations')
    expect(router.currentRoute.value.name).toBe('forbidden')
  })
})
