import type { DeviceDetailDto, DeviceSummaryDto, MeDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../../App.vue'
import type { AppLocale } from '../../../i18n'
import { mountWithApp } from '../../../test/app'
import { CATALOGUE, INTEGRATIONS, OBSERVED, ROOMS, basicHome } from '../../../test/devices'

// The device pages mounted in the real app (router, guards, i18n that throws on a missing
// key), in both languages and for each role, against a mocked API.

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  getHome: vi.fn(),
  listDevices: vi.fn(),
  listCapabilities: vi.fn(),
  listRooms: vi.fn(),
  listIntegrations: vi.fn(),
  getDevice: vi.fn(),
  sendCommand: vi.fn(),
  setEndpointFavorite: vi.fn(),
  getAttributeHistory: vi.fn(),
  listEnergyHourly: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

// jsdom lays nothing out, so ECharts can't draw: a stand-in that shows how many points a chart was given.
vi.mock('vue-echarts', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    default: defineComponent({
      name: 'VChart',
      props: { option: { type: Object, required: true } },
      setup: (props) => () =>
        h('div', {
          'data-slot': 'vchart',
          'data-series': JSON.stringify(
            (props.option.series as { name?: string; data: unknown[] }[]).map((entry) => entry.data.length),
          ),
        }),
    }),
  }
})

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

/** jsdom lays nothing out: gives an element the box it would have on screen. */
function stubBox(element: Element, box: { left: number; top: number; width: number; height: number }): void {
  const rect = { ...box, x: box.left, y: box.top, right: box.left + box.width, bottom: box.top + box.height }
  element.getBoundingClientRect = () => ({ ...rect, toJSON: () => rect }) as DOMRect
}

/** One finger (pointer 7) touching, moving on, lifting from or being taken off `element` by a scroll. */
function touch(
  element: Element,
  type: 'pointerdown' | 'pointermove' | 'pointerup' | 'pointercancel',
  clientX: number,
  clientY: number,
): void {
  element.dispatchEvent(
    new PointerEvent(type, {
      pointerId: 7,
      pointerType: 'touch',
      button: 0,
      clientX,
      clientY,
      bubbles: true,
    }),
  )
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
    expect(states[0]).toContain('80%')
    expect(states[2]).toContain(locale === 'en' ? 'Cooling to 22\u00a0°C' : 'Resfriando até 22\u00a0°C')
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
    expect(api.sendCommand).toHaveBeenCalledWith(
      expect.objectContaining({
        path: { id: 'e-light' },
        body: expect.objectContaining({ capabilityId: 'switch', command: 'turnOff', arguments: null }),
      }),
    )
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
    // One power model: the switch is the climate card's header toggle (no card of its own, no "Off" mode).
    expect(wrapper.find('[data-capability="switch"]').exists()).toBe(false)
    expect(
      wrapper.find('[data-capability="thermostat"] [data-slot="power-toggle"] [role="switch"]').exists(),
    ).toBe(true)
    const dial = wrapper.get('[data-capability="thermostat"] [role="slider"]')
    expect(dial.attributes('aria-valuenow')).toBe('22')
    expect(dial.attributes('aria-valuemin')).toBe('16')
    expect(dial.attributes('aria-valuemax')).toBe('30')
    // The constrained modes only (no "dry"), without "off"; the fan speeds as a second segmented row.
    expect(wrapper.findAll('[data-slot="thermostat-modes"] [data-slot="toggle-group-item"]')).toHaveLength(4)
    expect(wrapper.find('[data-slot="thermostat-fan"]').exists()).toBe(true)
    // The room temperature is on the dial, so no separate temperature card.
    expect(wrapper.findAll('[data-slot="reading"]')).toHaveLength(0)
    // The device info starts closed; its table holds every attribute.
    expect(wrapper.get('[data-slot="device-info"]').attributes('open')).toBeUndefined()
    expect(wrapper.findAll('[data-slot="attributes-table"] tbody tr')).toHaveLength(6)
    // Attribute ids are for Admins.
    expect(wrapper.get('[data-slot="attributes-table"]').text()).toContain('#7')
    // Breadcrumb: home › Devices › device.
    expect(wrapper.get('[data-slot="breadcrumb"]').find('a[href="/devices"]').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('device page', () => {
  it('Admins edit the device from its menu; Members mark favorites with the star', async () => {
    signedIn('Admin', 'en')
    const admin = await open('/devices/d-light')
    expect(admin.wrapper.find('button[aria-label="More actions"]').exists()).toBe(true)
    expect(admin.wrapper.get('[data-slot="favorite-toggle"]').attributes('aria-pressed')).toBe('false')
    // Ids only for Admins.
    expect(admin.wrapper.get('[data-slot="attributes-table"]').text()).toContain('#1')
    admin.wrapper.unmount()

    signedIn('Member', 'en')
    const light = basicHome()[0]!
    api.setEndpointFavorite.mockResolvedValue(ok({ ...light.endpoints[0]!, favorite: true }))
    const member = await open('/devices/d-light')
    expect(member.wrapper.find('button[aria-label="More actions"]').exists()).toBe(false)
    expect(member.wrapper.get('[data-slot="attributes-table"]').text()).not.toContain('#1')
    const favorite = member.wrapper.get('[data-slot="favorite-toggle"]')
    expect(favorite.attributes('aria-label')).toBe('Favorite')
    await favorite.trigger('click')
    await flushPromises()
    expect(api.setEndpointFavorite).toHaveBeenCalledWith({
      path: { id: 'e-light' },
      body: { favorite: true },
    })
    expect(member.wrapper.get('[data-slot="favorite-toggle"]').attributes('aria-pressed')).toBe('true')
    // Power sits in the brightness card's header.
    expect(member.wrapper.find('[data-capability="level"] [data-slot="power-toggle"]').exists()).toBe(true)
    member.wrapper.unmount()
  })

  it('lets Members lock and unlock; a Viewer sees the state only', async () => {
    signedIn('Member', 'en')
    const member = await open('/devices/d-lock')
    expect(member.wrapper.get('[data-slot="lock-state"]').text()).toBe('Locked')
    // Locked: one quiet action, the one that needs the PIN.
    const actions = member.wrapper.findAll('[data-capability="lock"] button').map((button) => button.text())
    expect(actions).toEqual(['Unlock…'])
    member.wrapper.unmount()

    signedIn('Viewer', 'en')
    const viewer = await open('/devices/d-lock')
    expect(viewer.wrapper.get('[data-slot="lock-state"]').text()).toBe('Locked')
    expect(viewer.wrapper.findAll('[data-capability="lock"] button')).toHaveLength(0)
    expect(viewer.wrapper.text()).not.toContain('Unlock')
    viewer.wrapper.unmount()
  })

  it('on a touch screen, a finger on the dial or the brightness slider still scrolls the page', async () => {
    signedIn('Member', 'en')
    api.sendCommand.mockResolvedValue(ok({ commandId: 'c1' }, 202))
    const sent = () => api.sendCommand.mock.calls.map((call) => call[0].body)

    // The AC's dial (viewBox 240 × 240 drawn at 240 px; the ring has a radius of 96).
    const ac = await open('/devices/d-ac')
    const dial = document.querySelector<SVGSVGElement>('[data-slot="thermostat-dial"]')!
    stubBox(dial, { left: 0, top: 0, width: 240, height: 240 })
    const slider = ac.wrapper.get('[data-capability="thermostat"] [role="slider"]')
    // The reading in the middle is not a control.
    touch(dial, 'pointerdown', 120, 120)
    touch(dial, 'pointerup', 120, 120)
    // A swipe that starts on the ring and becomes a scroll changes nothing.
    touch(dial, 'pointerdown', 120, 24)
    touch(dial, 'pointermove', 121, 60)
    touch(dial, 'pointercancel', 121, 60)
    await flushPromises()
    expect(slider.attributes('aria-valuenow')).toBe('22')
    expect(api.sendCommand).not.toHaveBeenCalled()
    // A tap on the ring sets the target there (the top of the 16–30 °C dial).
    touch(dial, 'pointerdown', 120, 24)
    touch(dial, 'pointerup', 120, 24)
    await flushPromises()
    expect(sent()).toEqual([
      expect.objectContaining({ command: 'setTargetTemperature', arguments: { targetTemperature: 23 } }),
    ])
    ac.wrapper.unmount()
    api.sendCommand.mockClear()

    // The light's brightness (200 px wide, at 80%).
    const light = await open('/devices/d-light')
    const root = document.querySelector<HTMLElement>('[data-capability="level"] [data-slot="fill-slider"]')!
    stubBox(root, { left: 0, top: 0, width: 200, height: 44 })
    const track = root.firstElementChild!
    const thumb = light.wrapper.get('[data-capability="level"] [role="slider"]')
    // Up and down: the page scrolls (cancelled), or nothing happens (lifted elsewhere).
    touch(track, 'pointerdown', 100, 10)
    touch(track, 'pointermove', 103, 40)
    touch(track, 'pointercancel', 103, 40)
    touch(track, 'pointerdown', 100, 10)
    touch(track, 'pointermove', 102, 40)
    touch(track, 'pointerup', 102, 40)
    await flushPromises()
    expect(thumb.attributes('aria-valuenow')).toBe('80')
    expect(api.sendCommand).not.toHaveBeenCalled()
    // Sideways, it follows the finger and sends the value once, on release.
    touch(track, 'pointerdown', 100, 10)
    touch(track, 'pointermove', 110, 12)
    touch(track, 'pointermove', 120, 12)
    touch(track, 'pointerup', 120, 12)
    await flushPromises()
    // A tap sets the value where it landed (a quarter of the way along this light's 1–100% range).
    touch(track, 'pointerdown', 50, 10)
    touch(track, 'pointerup', 51, 10)
    await flushPromises()
    expect(sent()).toEqual([
      expect.objectContaining({ command: 'setLevel', arguments: { level: 60 } }),
      expect.objectContaining({ command: 'setLevel', arguments: { level: 26 } }),
    ])
    light.wrapper.unmount()
  })

  it('charts the recorded history up to now, and the energy used', async () => {
    signedIn('Member', 'en')
    // The plug's power is recorded (a measurement): statistics, then the raw changes since the last bucket.
    api.listCapabilities.mockResolvedValue(
      ok(
        CATALOGUE.map((capability) =>
          capability.id === 'power'
            ? {
                ...capability,
                attributes: capability.attributes.map((entry) => ({
                  ...entry,
                  recorded: true,
                  stateClass: 'measurement',
                })),
              }
            : capability,
        ),
      ),
    )
    const minute = 60_000
    // Within the last hour (the chart's window ends now).
    const bucket = Math.floor((Date.now() - 60 * minute) / (5 * minute)) * 5 * minute
    const at = (time: number, v: number, band?: [number, number]) => ({
      t: new Date(time).toISOString(),
      v,
      min: band?.[0] ?? null,
      max: band?.[1] ?? null,
      s: null,
    })
    api.getAttributeHistory.mockImplementation(({ query }: { query: { resolution: string } }) =>
      Promise.resolve(
        ok(
          query.resolution === 'raw'
            ? {
                attributeId: 4,
                resolution: 'raw',
                unit: 'W',
                points: [at(bucket + 10 * minute, 41), at(bucket + 12 * minute, 55)],
              }
            : {
                attributeId: 4,
                resolution: '5m',
                unit: 'W',
                points: [at(bucket, 40, [38, 44]), at(bucket + 5 * minute, 42, [40, 45])],
              },
        ),
      ),
    )
    api.listEnergyHourly.mockResolvedValue(
      ok([
        {
          endpointId: 'e-plug',
          hourStartUtc: '2026-10-21T10:00:00Z',
          wh: 120,
          source: 'counter',
          estimated: false,
        },
      ]),
    )

    const { wrapper } = await open('/devices/d-plug?tab=history')
    // The tab's charts load on their own (a separate chunk).
    const power = await vi.waitFor(() =>
      wrapper.get('[data-slot="attribute-history"][data-attribute-id="4"]'),
    )
    await vi.waitFor(() => power.get('[data-slot="history-chart"]'))
    // The last 24 hours (the server's default range), and the raw changes from the last bucket's end on.
    expect(api.getAttributeHistory).toHaveBeenCalledWith(
      expect.objectContaining({ path: { id: 4 }, query: { resolution: 'auto' } }),
    )
    expect(api.getAttributeHistory).toHaveBeenCalledWith(
      expect.objectContaining({
        path: { id: 4 },
        query: { from: new Date(bucket + 10 * minute).toISOString(), resolution: 'raw' },
      }),
    )
    expect(power.get('[data-slot="history-chart"]').attributes('data-points')).toBe('4')
    // The band (min, range) under the mean; the mean runs on with the latest value to now.
    expect(JSON.parse(power.get('[data-slot="vchart"]').attributes('data-series')!)).toEqual([4, 4, 5])
    expect(wrapper.get('[data-slot="energy-total"]').text()).toContain('120')
    wrapper.unmount()
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
