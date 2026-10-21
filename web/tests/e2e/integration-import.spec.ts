import type { Page } from '@playwright/test'
import {
  SIMULATED,
  completeSetup,
  createFloor,
  dismissToasts,
  expect,
  listDevices,
  mutationHeaders,
  resetApp,
  test,
  waitForLive,
  type SeededFloor,
} from './fixtures'
import { t } from './i18n'

// Task 1.3-B: an integration is added through its connector's setup flow (the Simulated hub: a settings form, then
// "press the button on the hub", which presses itself after 2 s), its devices are reviewed (kept or hidden, each in a
// suggested room), and the integration's page turns it off and on, signs it in again and removes it. A device its
// integration stops reporting is kept as orphaned, with a badge, and can hand everything over to another device.

let floor: SeededFloor

const roomId = (name: string) => floor.rooms.find((room) => room.name === name)!.id

/** The flow wizard's current step, once it shows `type`. */
const wizardAt = (page: Page, type: string) => page.locator(`[data-slot="flow-wizard"][data-step="${type}"]`)

/** Picks an option of a schema form's select (a combobox with its label). */
async function choose(page: Page, label: string, option: string): Promise<void> {
  await page.getByRole('combobox', { name: label }).click()
  await page.getByRole('option', { name: option, exact: true }).click()
}

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
  floor = await createFloor(page.request)
})

test('an admin adds the simulated home through its setup flow and reviews its devices', async ({
  page,
  gates,
}) => {
  await page.goto('/integrations')
  await expect(page.getByRole('heading', { name: t('integrations.empty.title') })).toBeVisible()
  await page.getByRole('link', { name: t('integrations.empty.browse') }).click()

  // The catalogue: every connector, by kind.
  await expect(page.getByRole('heading', { level: 1, name: t('integrations.catalog.title') })).toBeVisible()
  await gates.check('integrations catalogue')
  await page.locator('[data-connector="simulated"]').getByRole('link').click()

  // Step 1: the connector's settings form (the shared schema form).
  await expect(wizardAt(page, 'form')).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Devices' })).toBeVisible()
  await gates.check('setup flow, settings form')
  await choose(page, 'Devices', t('integrations.flow.options.preset.basic'))
  await wizardAt(page, 'form')
    .getByRole('button', { name: t('common.continue') })
    .click()

  // Step 2: waiting for the hub's button (polled), then connected.
  await expect(wizardAt(page, 'progress')).toBeVisible()
  await expect(page.getByText(t('integrations.flow.waiting'))).toBeVisible()
  await gates.check('setup flow, waiting for the hub')
  await expect(page.locator('[data-slot="import-review"]')).toBeVisible({ timeout: 20_000 })

  // The review: five devices, the light in the living room and the AC in the bedroom (suggested by the connector).
  const rows = page.locator('[data-slot="review-list"] > li')
  await expect(rows).toHaveCount(5, { timeout: 20_000 })
  await expect(
    page.getByRole('combobox', { name: t('integrations.review.room', { name: SIMULATED.light }) }),
  ).toContainText('Living room')
  await gates.check('import review')

  // Hide the door sensor; put the coffee maker in the kitchen.
  await page
    .getByRole('checkbox', { name: t('integrations.review.keep', { name: SIMULATED.contact }) })
    .click()
  await choose(page, t('integrations.review.room', { name: SIMULATED.plug }), 'Kitchen')
  await page.locator('[data-slot="review-finish"]').click()

  // The integration's page: its status and devices.
  await expect(page).toHaveURL(/\/integrations\/[0-9a-f-]{36}$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Simulated home' })).toBeVisible()
  await expect(page.locator('[data-slot="integration-devices"] li')).toHaveCount(5)
  const devices = await listDevices(page.request)
  const plug = devices.find((device) => device.name === SIMULATED.plug)!
  const contact = devices.find((device) => device.name === SIMULATED.contact)!
  expect((plug as unknown as { roomId: number | null }).roomId).toBe(roomId('Kitchen'))
  expect(
    (contact as unknown as { endpoints: { hidden: boolean }[] }).endpoints.every(
      (endpoint) => endpoint.hidden,
    ),
  ).toBe(true)
  // The hidden door sensor is still listed here, marked as hidden.
  await expect(
    page
      .locator('[data-slot="integration-devices"] li')
      .filter({ hasText: SIMULATED.contact })
      .locator('[data-slot="device-hidden"]'),
  ).toBeVisible()
  await dismissToasts(page)
  await gates.check('integration page')
})

test('the integration page turns it off and on, signs it in again and removes it', async ({
  page,
  gates,
}) => {
  const created = await page.request.post('/api/integrations', {
    headers: mutationHeaders(),
    data: { connectorKey: 'simulated', name: 'Simulated home', config: { preset: 'basic' } },
  })
  expect(created.status(), await created.text()).toBe(201)
  const { id } = (await created.json()) as { id: string }
  await expect.poll(async () => (await listDevices(page.request)).length, { timeout: 30_000 }).toBe(5)

  await page.goto(`/integrations/${id}`)
  await waitForLive(page)
  const enabled = page.locator('[data-slot="integration-enabled"]')
  await expect(enabled).toHaveAttribute('aria-checked', 'true')

  // Off: it stops talking to its devices (they stay); then on again.
  await enabled.click()
  await expect(enabled).toHaveAttribute('aria-checked', 'false')
  await expect(page.getByText(t('integrationStatus.Stopped')).first()).toBeVisible()
  await expect(page.locator('[data-slot="integration-devices"] li')).toHaveCount(5)
  await enabled.click()
  await expect(enabled).toHaveAttribute('aria-checked', 'true')
  await dismissToasts(page)

  // Sign in again: the same flow, filled with its settings; the devices are kept.
  await page
    .getByRole('link', { name: t('integrations.detail.reauth') })
    .first()
    .click()
  await expect(wizardAt(page, 'form')).toBeVisible()
  await wizardAt(page, 'form')
    .getByRole('button', { name: t('common.continue') })
    .click()
  await expect(page).toHaveURL(new RegExp(`/integrations/${id}$`), { timeout: 20_000 })
  await expect(page.getByText(t('integrations.setup.reauthDone', { name: 'Simulated home' }))).toBeVisible()
  await expect(page.locator('[data-slot="integration-devices"] li')).toHaveCount(5)
  await dismissToasts(page)

  // Remove: asks first, then everything it brought goes.
  await page.locator('[data-slot="integration-remove"]').click()
  const dialog = page.getByRole('alertdialog', {
    name: t('integrations.delete.title', { name: 'Simulated home' }),
  })
  await expect(dialog).toBeVisible()
  await gates.check('remove integration dialog')
  await dialog.getByRole('button', { name: t('integrations.delete.confirm') }).click()
  await expect(page).toHaveURL(/\/integrations$/)
  await expect(page.getByRole('heading', { name: t('integrations.empty.title') })).toBeVisible()
  expect(await listDevices(page.request)).toHaveLength(0)
})

test('a device the integration stops reporting is kept as orphaned and can be replaced', async ({
  page,
  gates,
}) => {
  // The demo preset's twenty devices; signed in again with the basic preset, fifteen of them are no longer reported.
  const created = await page.request.post('/api/integrations', {
    headers: mutationHeaders(),
    data: { connectorKey: 'simulated', name: 'Demo home', config: { preset: 'demo' } },
  })
  expect(created.status(), await created.text()).toBe(201)
  const { id } = (await created.json()) as { id: string }
  await expect.poll(async () => (await listDevices(page.request)).length, { timeout: 30_000 }).toBe(20)

  await page.goto(`/integrations/${id}/reauth`)
  await expect(wizardAt(page, 'form')).toBeVisible()
  await choose(page, 'Devices', t('integrations.flow.options.preset.basic'))
  await wizardAt(page, 'form')
    .getByRole('button', { name: t('common.continue') })
    .click()
  await expect(page).toHaveURL(new RegExp(`/integrations/${id}$`), { timeout: 20_000 })

  // Nothing is deleted: the fifteen are flagged on the integration's page and in the device grid.
  type Listed = { id: string; name: string; isOrphaned: boolean }
  let orphaned: Listed[] = []
  await expect
    .poll(
      async () => {
        orphaned = ((await listDevices(page.request)) as unknown as Listed[]).filter((d) => d.isOrphaned)
        return orphaned.length
      },
      { timeout: 30_000 },
    )
    .toBe(15)
  await expect(page.locator('[data-slot="integration-devices"] li')).toHaveCount(20)
  // A badge, or on phones the line under the name.
  await expect(
    page
      .locator('[data-slot="integration-devices"]')
      .getByText(t('device.orphaned'))
      .filter({ visible: true })
      .first(),
  ).toBeVisible()
  await dismissToasts(page)
  await gates.check('integration with orphaned devices')

  // An orphaned device says so, and an admin hands it over to another device.
  const old = orphaned.find((device) => device.name === 'Washing machine')!
  expect(old, 'the washer is one of the orphaned devices').toBeDefined()
  await page.goto(`/devices/${old.id}`)
  await expect(page.getByRole('heading', { level: 1, name: old.name })).toBeVisible()
  await expect(page.locator('[data-slot="orphaned-notice"]')).toBeVisible()
  await page.locator('[data-slot="replace-device"]').click()
  const dialog = page.getByRole('dialog', { name: t('devices.replace.title', { name: old.name }) })
  await expect(dialog).toBeVisible()
  await gates.check('replace device dialog')
  const target = (await listDevices(page.request)).find((device) => device.name === SIMULATED.plug)!
  await dialog.locator(`[data-candidate="${target.id}"]`).click()
  await expect(dialog.locator('[data-slot="replace-summary"]')).toBeVisible()
  await dialog.locator('[data-slot="replace-confirm"]').click()

  // The new device takes over the old one's name; the old one is gone.
  await expect(page).toHaveURL(new RegExp(`/devices/${target.id}`))
  await expect(page.getByRole('heading', { level: 1, name: old.name })).toBeVisible()
  const after = (await listDevices(page.request)) as unknown as Listed[]
  expect(after.find((device) => device.id === old.id)).toBeUndefined()
  expect(after.find((device) => device.id === target.id)?.name).toBe(old.name)
})
