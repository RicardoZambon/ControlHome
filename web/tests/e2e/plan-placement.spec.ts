import type { APIRequestContext, Page } from '@playwright/test'
import {
  SIMULATED,
  attributeValue,
  completeSetup,
  createFloor,
  createSimulatedHome,
  expect,
  isPhone,
  listPlacements,
  placeEndpoint,
  planPoint,
  resetApp,
  tapPlan,
  test,
  waitForLive,
  type SeededFloor,
  type SimulatedDevice,
} from './fixtures'
import { t } from './i18n'

// Task 1.3-A: devices on the floor plan. The editor's Devices tab places them (dragged from the list, or "Place" and a
// tap on the plan), moves them (dragging their marker) and takes them off the plan; the server puts each one in the room
// under its point. In view mode a tap on a device opens its controls. Every change is checked on the server too.

type Home = Awaited<ReturnType<typeof createSimulatedHome>>

let floor: SeededFloor
let home: Home

const roomId = (name: string) => floor.rooms.find((room) => room.name === name)!.id

/** The placement of a device's first endpoint, from the server. */
async function placementOf(request: APIRequestContext, device: SimulatedDevice) {
  return (await listPlacements(request)).find((entry) => entry.endpointId === device.endpoints[0]!.id)
}

/** The room the server keeps for a device's first endpoint (`effectiveRoomId`). */
async function endpointRoom(request: APIRequestContext, device: SimulatedDevice): Promise<number | null> {
  const response = await request.get(`/api/devices/${device.id}`)
  expect(response.status()).toBe(200)
  const detail = (await response.json()) as { endpoints: { effectiveRoomId: number | null }[] }
  return detail.endpoints[0]!.effectiveRoomId
}

const marker = (page: Page) => page.locator('[data-slot="placed-device"]')
const unplacedRow = (page: Page, name: string) =>
  page.locator('[data-slot="unplaced-device"]').filter({ hasText: name })

/** Waits for the next `PUT /api/placements` (a device placed or moved). */
const placementSaved = (page: Page) =>
  page.waitForResponse(
    (response) => response.request().method() === 'PUT' && response.url().endsWith('/api/placements'),
  )

/** Drags with the mouse from one screen point to another, in small steps (as a hand would). */
async function drag(page: Page, from: { x: number; y: number }, to: { x: number; y: number }): Promise<void> {
  await page.mouse.move(from.x, from.y)
  await page.mouse.down()
  await page.mouse.move(from.x + 8, from.y + 8, { steps: 4 })
  await page.mouse.move(to.x, to.y, { steps: 12 })
  await page.mouse.up()
}

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
  floor = await createFloor(page.request)
  home = await createSimulatedHome(page.request)
})

test('an admin drags devices onto the plan, moves one to another room and takes one off', async ({
  page,
  gates,
}) => {
  test.skip(isPhone(page), 'Phones place devices with a tap (the next test).')
  await page.goto(`/floors/${floor.id}/edit?tab=devices`)
  await expect(page.getByRole('tab', { name: t('floors.editor.tabs.devices') })).toHaveAttribute(
    'aria-selected',
    'true',
  )
  await expect(unplacedRow(page, SIMULATED.plug)).toBeVisible()
  await gates.check('editor, Devices tab')

  // Drag the plug by its handle into the kitchen.
  const handle = unplacedRow(page, SIMULATED.plug).locator('[data-slot="drag-handle"]')
  const box = (await handle.boundingBox())!
  let saved = placementSaved(page)
  await drag(page, { x: box.x + box.width / 2, y: box.y + box.height / 2 }, await planPoint(page, 1450, 500))
  expect((await saved).status()).toBe(200)
  await expect(marker(page)).toHaveCount(1)
  await expect(unplacedRow(page, SIMULATED.plug)).toHaveCount(0)
  await expect.poll(async () => (await placementOf(page.request, home.plug))?.roomId).toBe(roomId('Kitchen'))
  expect(await endpointRoom(page.request, home.plug)).toBe(roomId('Kitchen'))

  // "Place" the light, then tap its spot in the living room.
  await page
    .getByRole('button', { name: t('floors.editor.devices.placeNamed', { name: SIMULATED.light }) })
    .click()
  await expect(page.locator('[data-slot="place-hint"]')).toContainText(SIMULATED.light)
  saved = placementSaved(page)
  await tapPlan(page, 500, 400, false)
  expect((await saved).status()).toBe(200)
  await expect(marker(page)).toHaveCount(2)
  await expect
    .poll(async () => (await placementOf(page.request, home.light))?.roomId)
    .toBe(roomId('Living room'))
  await gates.check('editor with two devices placed')

  // Move the light into the bedroom by dragging its marker: the room follows its spot.
  saved = placementSaved(page)
  await drag(page, await planPoint(page, 500, 400), await planPoint(page, 500, 1100))
  expect((await saved).status()).toBe(200)
  await expect.poll(async () => (await placementOf(page.request, home.light))?.roomId).toBe(roomId('Bedroom'))
  expect(await endpointRoom(page.request, home.light)).toBe(roomId('Bedroom'))
  const moved = (await placementOf(page.request, home.light))!
  expect(moved.yCm).toBeGreaterThan(1000)

  // Take the plug off the plan: it stays in its room, and is back in the list.
  await page
    .getByRole('button', { name: t('floors.editor.devices.removeNamed', { name: SIMULATED.plug }) })
    .click()
  await expect(marker(page)).toHaveCount(1)
  await expect(unplacedRow(page, SIMULATED.plug)).toBeVisible()
  await expect.poll(async () => placementOf(page.request, home.plug)).toBeUndefined()
  expect(await endpointRoom(page.request, home.plug)).toBe(roomId('Kitchen'))

  // After a reload the plan shows what the server kept.
  await page.reload()
  await expect(marker(page)).toHaveCount(1)
  await expect(page.locator('[data-slot="placed-list"]')).toContainText(SIMULATED.light)
})

test('on a phone a device is placed with Place and a tap on the plan', async ({ page, gates }) => {
  test.skip(!isPhone(page), 'Wider screens drag devices onto the plan (the test above).')
  await page.goto(`/floors/${floor.id}/edit?tab=devices`)
  // The panel is a sheet on phones: open it, choose the device, and the sheet steps aside for the plan.
  await page.getByRole('button', { name: t('floors.editor.devices.placeShort') }).click()
  await expect(unplacedRow(page, SIMULATED.light)).toBeVisible()
  await gates.check('editor Devices sheet on a phone')
  await page
    .getByRole('button', { name: t('floors.editor.devices.placeNamed', { name: SIMULATED.light }) })
    .click()
  await expect(page.locator('[data-slot="place-hint"]')).toContainText(SIMULATED.light)
  // The sheet has stepped aside (its overlay is gone), so the next tap reaches the plan.
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const saved = placementSaved(page)
  await tapPlan(page, 500, 400, true)
  expect((await saved).status()).toBe(200)
  await expect(marker(page)).toHaveCount(1)
  await expect
    .poll(async () => (await placementOf(page.request, home.light))?.roomId)
    .toBe(roomId('Living room'))
})

test('a tap on a device on the plan opens its controls, live', async ({ page, gates }) => {
  await placeEndpoint(page.request, home.light, floor.id, 500, 400)
  await placeEndpoint(page.request, home.ac, floor.id, 500, 1100)
  await page.goto(`/floors/${floor.id}`)
  await waitForLive(page)
  await expect(marker(page)).toHaveCount(2)
  await expect(page.getByRole('button', { name: new RegExp(`^${SIMULATED.light}`) })).toBeVisible()

  await tapPlan(page, 500, 400, isPhone(page))
  const panel = page.locator('[data-slot="device-quick-panel"]')
  await expect(panel).toBeVisible()
  await expect(panel.getByRole('heading', { name: SIMULATED.light })).toBeVisible()
  await gates.check('device controls on the plan')

  // Switch the light from the panel: the server, and the marker's state ring, follow.
  const power = panel.getByRole('switch', { name: t('device.switch.label', { name: SIMULATED.light }) })
  const wasOn = (await power.getAttribute('aria-checked')) === 'true'
  await power.click()
  await expect(power).toHaveAttribute('aria-checked', String(!wasOn))
  await expect.poll(() => attributeValue(page.request, SIMULATED.light, 'switch', 'on')).toBe(!wasOn)
  const lightMarker = marker(page).filter({ has: page.locator('title', { hasText: SIMULATED.light }) })
  await expect(lightMarker).toHaveAttribute('data-tone', wasOn ? 'off' : 'on')

  // The device page is one tap away.
  await panel.getByRole('link', { name: t('floorplan.devices.openDevice') }).click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.light })).toBeVisible()
})
