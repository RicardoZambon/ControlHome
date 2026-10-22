import type { Locator, Page } from '@playwright/test'
import {
  attributeValue,
  expect,
  listPlacements,
  reportAvailability,
  resetApp,
  setUpDemoHome,
  test,
  waitForLive,
  type SimulatedDevice,
} from './fixtures'
import { t, tr } from './i18n'

// Task 1.3-C: the dashboard over the demo home, loaded by the setup's "Load demo home" option: the first floor's plan
// with its devices, the six favorites as tiles, the eight rooms (what is on, temperature, power), the power drawn now,
// the offline devices and the integration's health. Everything follows the devices live.

/** The demo home's favorites (DemoHome.Favorites), by their English names. */
const FAVORITES = [
  'Living room light',
  'Bedroom air conditioner',
  'Front door lock',
  'Gate',
  'Living room TV',
  'Coffee maker plug',
]

/** The demo home's rooms (DemoHome.Floors): the ground floor's, then the upper floor's. */
const ROOMS = ['Garage', 'Living room', 'Kitchen', 'Laundry', 'Bedroom', 'Suite', 'Office', 'Bathroom']

const byName = (devices: SimulatedDevice[], name: string) => {
  const device = devices.find((entry) => entry.name === name)
  expect(device, `the demo home has "${name}"`).toBeDefined()
  return device!
}

/** The figure of one "at a glance" tile (`power`, `active`, `offline`, `integrations`). */
const stat = (page: Page, name: string): Locator =>
  page.locator(`[data-stat="${name}"] [data-slot="stat-value"]`)

const roomCard = (page: Page, name: string): Locator =>
  page.locator('[data-slot="room-card"]').filter({ hasText: name })

const favoriteTile = (page: Page, name: string): Locator =>
  page.locator('[data-slot="dashboard-favorites"] [data-slot="device-tile"]').filter({ hasText: name })

/** A device's marker on the dashboard's plan (compact markers have no name: found by their placement). */
async function planMarker(page: Page, device: SimulatedDevice): Promise<Locator> {
  const placement = (await listPlacements(page.request)).find(
    (entry) => entry.endpointId === device.endpoints[0]!.id,
  )
  expect(placement, `${device.name} is on the plan`).toBeDefined()
  return page.locator(
    `[data-slot="dashboard-plan"] [data-slot="placed-device"][data-placement-id="${placement!.id}"]`,
  )
}

test.beforeEach(async ({ request }) => {
  await resetApp(request)
})

test('the demo home fills the dashboard: its plan, favorites, rooms, power and integration', async ({
  page,
  gates,
}) => {
  const devices = await setUpDemoHome(page.request)
  await page.goto('/')
  await waitForLive(page)

  // The first floor (ground) with its eighteen devices on it, live.
  const plan = page.locator('[data-slot="dashboard-plan"]')
  await expect(plan).toContainText('Ground floor')
  await expect(plan.locator('[data-slot="placed-device"]')).toHaveCount(18)
  // A full home: no first steps.
  await expect(page.locator('[data-slot="dashboard-start"]')).toHaveCount(0)

  // The favorites, as device tiles.
  const tiles = page.locator('[data-slot="dashboard-favorites"] [data-slot="device-tile"]')
  await expect(tiles).toHaveCount(FAVORITES.length)
  for (const name of FAVORITES) await expect(favoriteTile(page, name)).toHaveCount(1)

  // Every room of both floors, with the floor's name (the home has two).
  const cards = page.locator('[data-slot="room-card"]')
  await expect(cards).toHaveCount(ROOMS.length)
  for (const name of ROOMS) await expect(roomCard(page, name).first()).toBeVisible()
  await expect(roomCard(page, 'Bathroom')).toContainText('Upper floor')
  // The kitchen's plugs draw power; the bathroom's sensor gives its temperature.
  await expect(roomCard(page, 'Kitchen').locator('[data-slot="room-power"]')).toHaveText(/\d+(\.\d+)?\s*k?W/)
  await expect(roomCard(page, 'Bathroom').locator('[data-slot="room-temperature"]')).toHaveText(/\d.*°C/)

  // At a glance: the power drawn now (from every meter), nothing offline, the integration working. Lists of offline
  // devices and of integrations show up only when something needs someone.
  await expect(stat(page, 'power')).toHaveText(/^\s*\d[\d.,]*\s*$/)
  await expect(page.locator('[data-stat="power"]')).toContainText(/k?W/)
  await expect(stat(page, 'offline')).toHaveText('0')
  await expect(page.locator('[data-stat="offline"]')).toContainText(t('dashboard.glance.allReachable'))
  await expect(page.locator('[data-slot="dashboard-offline"]')).toHaveCount(0)
  await expect(stat(page, 'integrations')).toHaveText('1')
  await expect(page.locator('[data-stat="integrations"]')).toContainText(t('dashboard.glance.allConnected'))
  await expect(page.locator('[data-slot="integration-chip"]')).toHaveCount(0)
  // Twenty devices (the 3-gang switch is one device with three controls).
  await expect(page.locator('[data-stat="active"]')).toContainText(
    t('dashboard.glance.activeHint', { total: 20 }).split(' | ')[2]!,
  )
  await gates.check('dashboard with the demo home')

  // Switching the living room light from its tile: "On now" and the living room follow at once.
  const light = byName(devices, 'Living room light')
  const wasOn = (await attributeValue(page.request, light.name, 'switch', 'on')) === true
  const activeBefore = Number(await stat(page, 'active').innerText())
  const livingRoomActive = roomCard(page, 'Living room').locator('p').first()
  const livingBefore = Number(/^\s*(\d+)/.exec(await livingRoomActive.innerText())![1])
  const power = favoriteTile(page, light.name).getByRole('switch', {
    name: t('device.switch.label', { name: light.name }),
  })
  await expect(power).toHaveAttribute('aria-checked', String(wasOn))
  await power.click()
  await expect(power).toHaveAttribute('aria-checked', String(!wasOn))
  await expect.poll(() => attributeValue(page.request, light.name, 'switch', 'on')).toBe(!wasOn)
  const step = wasOn ? -1 : 1
  await expect(stat(page, 'active')).toHaveText(String(activeBefore + step))
  await expect(livingRoomActive).toHaveText(new RegExp(`^\\s*${livingBefore + step} `))
  await expect(await planMarker(page, light)).toHaveAttribute('data-tone', wasOn ? 'off' : 'on')

  // A room card opens the devices of that room.
  await roomCard(page, 'Kitchen').click()
  await expect(page).toHaveURL(/\/devices\?room=\d+$/)
  await expect(
    page.locator('[data-slot="device-tile"]').filter({ hasText: 'Coffee maker plug' }),
  ).toBeVisible()
})

test('a device that goes offline is listed on the dashboard until it is back', async ({ page, gates }) => {
  const devices = await setUpDemoHome(page.request)
  const plug = byName(devices, 'Coffee maker plug')
  await page.goto('/')
  await waitForLive(page)
  await expect(stat(page, 'offline')).toHaveText('0')
  await expect(favoriteTile(page, plug.name).getByRole('switch')).toBeVisible()

  // The plug stops answering: counted, listed with its room and since when, and its tile, room and marker say so.
  await reportAvailability(page.request, plug, 'Offline')
  await expect(stat(page, 'offline')).toHaveText('1')
  const offline = page.locator('[data-slot="dashboard-offline"] li')
  await expect(offline).toHaveCount(1)
  await expect(offline).toContainText(plug.name)
  await expect(offline).toContainText('Kitchen')
  await expect(offline).toContainText(t('dashboard.offline.since', { time: '' }).trim())
  await expect(roomCard(page, 'Kitchen').locator('[data-slot="room-offline"]')).toBeVisible()
  // The tile leads to the list.
  await page.locator('[data-stat="offline"]').click()
  await expect(page).toHaveURL(/#dashboard-offline$/)
  await expect(page.locator('[data-slot="dashboard-offline"]')).toBeInViewport()
  await expect(favoriteTile(page, plug.name).locator('[data-slot="tile-offline"]')).toBeVisible()
  await expect(favoriteTile(page, plug.name).getByRole('switch')).toHaveCount(0)
  await expect(await planMarker(page, plug)).toHaveAttribute('data-tone', 'offline')
  await gates.check('dashboard with an offline device')

  // A reload shows the same (the summary comes from the server, not only from the live connection).
  await page.reload()
  await expect(offline).toHaveCount(1)
  await expect(stat(page, 'offline')).toHaveText('1')

  // Its entry opens the device.
  await offline.getByRole('link').click()
  await expect(page.getByRole('heading', { level: 1, name: plug.name })).toBeVisible()

  // Back online: off the list.
  await page.goto('/')
  await waitForLive(page)
  await reportAvailability(page.request, plug, 'Online')
  await expect(stat(page, 'offline')).toHaveText('0')
  await expect(offline).toHaveCount(0)
  await expect(page.locator('[data-slot="dashboard-offline"]')).toHaveCount(0)
  await expect(favoriteTile(page, plug.name).getByRole('switch')).toBeVisible()
})

test('a demo home set up in Portuguese is named in Portuguese', async ({ page, gates }) => {
  const l = (key: string, params?: Record<string, string | number>) => tr('pt-BR', key, params)
  await setUpDemoHome(page.request, 'pt-BR')
  await page.goto('/')
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')
  await expect(page.locator('[data-slot="dashboard-plan"]')).toContainText('Térreo')
  await expect(page.locator('[data-stat="power"]')).toContainText(l('dashboard.glance.power'))
  await expect(favoriteTile(page, 'Luz da sala')).toHaveCount(1)
  await expect(page.locator('[data-slot="room-card"]')).toHaveCount(ROOMS.length)
  for (const name of [
    'Garagem',
    'Sala',
    'Cozinha',
    'Lavanderia',
    'Quarto',
    'Suíte',
    'Escritório',
    'Banheiro',
  ])
    await expect(roomCard(page, name)).toHaveCount(1)
  await expect(page.locator('[data-stat="integrations"]')).toContainText(l('dashboard.glance.allConnected'))
  await gates.check('dashboard with the demo home in Portuguese')
})
