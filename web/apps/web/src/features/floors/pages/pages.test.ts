import type { FloorDto, MeDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../../App.vue'
import type { AppLocale } from '../../../i18n'
import { mountWithApp } from '../../../test/app'

// The floor pages mounted in the real app (router, guards, i18n that throws on a missing key), in both languages and
// for an Admin and a Member, against a mocked API.

const api = vi.hoisted(() => ({
  getSetupStatus: vi.fn(),
  getMe: vi.fn(),
  getHome: vi.fn(),
  listFloors: vi.fn(),
  getFloor: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })

const square = (x: number, y: number, size: number) => [
  { x, y },
  { x: x + size, y },
  { x: x + size, y: y + size },
  { x, y: y + size },
]

const floor: FloorDto = {
  id: 3,
  name: 'Térreo',
  level: 0,
  widthCm: 2000,
  heightCm: 1500,
  gridCm: 50,
  background: null,
  rowVersion: 'AAAAAAAAB9E=',
  rooms: [
    {
      id: 1,
      name: 'Sala',
      icon: 'sofa',
      colorToken: 'room-1',
      polygon: square(0, 0, 500),
      areaM2: 25,
      sortOrder: 0,
    },
    {
      id: 2,
      name: 'Cozinha',
      icon: 'cooking-pot',
      colorToken: 'room-2',
      polygon: square(500, 0, 400),
      areaM2: 16,
      sortOrder: 1,
    },
  ],
}

function signedIn(role: 'Admin' | 'Member', locale: AppLocale): void {
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
  api.listFloors.mockResolvedValue(ok([{ id: 3, name: 'Térreo', level: 0, roomCount: 2 }]))
  api.getFloor.mockResolvedValue(ok(floor))
})

afterEach(() => {
  document.body.innerHTML = ''
})

describe.each(['en', 'pt-BR'] as const)('floor pages (%s)', (locale) => {
  it('lists the floors with a preview', async () => {
    signedIn('Admin', locale)
    const { wrapper } = await open('/floors', locale)
    expect(wrapper.find('h1').text()).toBe(locale === 'en' ? 'Floors' : 'Andares')
    expect(wrapper.text()).toContain('Térreo')
    expect(wrapper.findAll('[data-room-key]')).toHaveLength(2)
    wrapper.unmount()
  })

  it('shows a floor with its rooms, and the editor link to an Admin', async () => {
    signedIn('Admin', locale)
    const { wrapper } = await open('/floors/3', locale)
    expect(wrapper.find('h1').text()).toBe('Térreo')
    expect(wrapper.find('svg[role="group"]').exists()).toBe(true)
    // Rooms are keyboard-reachable buttons named by name and area (jsdom has no layout, so labels stay hidden).
    expect(wrapper.findAll('svg [role="button"]').map((room) => room.attributes('aria-label'))).toEqual([
      'Sala, 25 m²',
      'Cozinha, 16 m²',
    ])
    expect(wrapper.find('a[href="/floors/3/edit"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('opens the editor for an Admin', async () => {
    signedIn('Admin', locale)
    const { wrapper, router } = await open('/floors/3/edit', locale)
    expect(router.currentRoute.value.name).toBe('floor-edit')
    expect(wrapper.find('h1').text()).toContain('Térreo')
    expect(wrapper.find('[role="toolbar"]').exists()).toBe(true)
    expect(wrapper.findAll('[data-room-key]')).toHaveLength(2)
    wrapper.unmount()
  })

  it('places the floor in the top bar: home › Floors › floor (› Edit plan)', async () => {
    signedIn('Admin', locale)
    const floors = locale === 'en' ? 'Floors' : 'Andares'
    const view = await open('/floors/3', locale)
    const trail = view.wrapper.find('[data-slot="breadcrumb"]')
    expect(trail.find('a[href="/floors"]').text()).toBe(floors)
    expect(trail.find('[aria-current="page"]').text()).toBe('Térreo')
    view.wrapper.unmount()

    const editor = await open('/floors/3/edit', locale)
    const editTrail = editor.wrapper.find('[data-slot="breadcrumb"]')
    expect(editTrail.find('a[href="/floors/3"]').text()).toBe('Térreo')
    expect(editTrail.find('[aria-current="page"]').text()).toBe(
      locale === 'en' ? 'Edit plan' : 'Editar planta',
    )
    editor.wrapper.unmount()
  })
})

describe('floor editor', () => {
  it('Save waits for a change: no changes, nothing to save', async () => {
    signedIn('Admin', 'en')
    const { wrapper } = await open('/floors/3/edit')
    const save = wrapper.findAll('button').find((button) => button.text().startsWith('Save'))!
    expect(save.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('No changes')
    expect(wrapper.text()).not.toContain('All changes saved')
    wrapper.unmount()
  })

  it('is a focused task: the bottom tab bar steps aside', async () => {
    signedIn('Admin', 'en')
    const view = await open('/floors/3')
    expect(view.wrapper.find('nav[aria-label="Sections"]').exists()).toBe(true)
    view.wrapper.unmount()
    const editor = await open('/floors/3/edit')
    expect(editor.wrapper.find('nav[aria-label="Sections"]').exists()).toBe(false)
    editor.wrapper.unmount()
  })
})

describe('the traced background image', () => {
  const traced: FloorDto = {
    ...floor,
    background: { fileId: 'f1', opacity: 0.35, offsetXCm: 0, offsetYCm: 0, cmPerPx: 2.5 },
  }

  it('is a tracing aid: off in previews and in view mode until asked for', async () => {
    signedIn('Admin', 'en')
    api.getFloor.mockResolvedValue(ok(traced))
    const list = await open('/floors')
    expect(list.wrapper.findAll('[data-room-key]')).toHaveLength(2)
    expect(list.wrapper.find('[data-slot="floor-background"]').exists()).toBe(false)
    list.wrapper.unmount()

    const { wrapper } = await open('/floors/3')
    expect(wrapper.find('[data-slot="floor-background"]').exists()).toBe(false)
    const toggle = wrapper.find('[role="switch"]')
    expect(toggle.attributes('aria-checked')).toBe('false')
    await toggle.trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-slot="floor-background"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('shows in the editor', async () => {
    signedIn('Admin', 'en')
    api.getFloor.mockResolvedValue(ok(traced))
    const { wrapper } = await open('/floors/3/edit')
    expect(wrapper.find('[data-slot="floor-background"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('has no switch on a floor without one', async () => {
    signedIn('Admin', 'en')
    const { wrapper } = await open('/floors/3')
    expect(wrapper.find('[role="switch"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('floor pages for a Member', () => {
  it('shows the plan without the editor link, and keeps the editor closed', async () => {
    signedIn('Member', 'en')
    const { wrapper, router } = await open('/floors/3')
    expect(wrapper.find('a[href="/floors/3/edit"]').exists()).toBe(false)
    await router.push('/floors/3/edit')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('forbidden')
    wrapper.unmount()
  })

  it('shows the empty state without the add button', async () => {
    signedIn('Member', 'en')
    api.listFloors.mockResolvedValue(ok([]))
    const { wrapper } = await open('/floors')
    expect(wrapper.text()).toContain('No floors have been drawn yet')
    expect(wrapper.text()).not.toContain('Add a floor')
    wrapper.unmount()
  })
})
