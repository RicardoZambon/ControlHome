import type { Page } from '@playwright/test'
import {
  SIMULATED,
  completeSetup,
  createSimulatedHome,
  createUser,
  expect,
  mutationHeaders,
  resetApp,
  signIn,
  test,
  testData,
  waitForLive,
} from './fixtures'
import { t } from './i18n'

// Task 1.2-E: live updates over SignalR. Two people (two browser contexts) look at the same devices; what one changes
// appears on the other's screen within 2 s, without a reload. A change made outside the browser (the API) arrives the
// same way.

const LIVE_MS = 2_000

const powerOf = (page: Page, name: string) =>
  page
    .locator('[data-slot="device-tile"]')
    .filter({ hasText: name })
    .getByRole('switch', { name: t('device.switch.label', { name }) })

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
  await createSimulatedHome(page.request)
  await createUser(page.request, testData.member, 'Member')
})

test('a change in one browser appears in the other within 2 s', async ({ page, browser, gates }) => {
  // The admin's browser.
  await page.goto('/devices')
  await waitForLive(page)

  // The member's browser: a separate context (its own cookies, its own hub connection).
  const memberContext = await browser.newContext()
  try {
    const other = await memberContext.newPage()
    await signIn(other.request, testData.member)
    await other.goto('/devices')
    await waitForLive(other)

    // The admin switches the light: the member sees it flip.
    const lightHere = powerOf(page, SIMULATED.light)
    const lightThere = powerOf(other, SIMULATED.light)
    const lightWasOn = (await lightHere.getAttribute('aria-checked')) === 'true'
    await expect(lightThere).toHaveAttribute('aria-checked', String(lightWasOn))
    await lightHere.click()
    await expect(lightThere).toHaveAttribute('aria-checked', String(!lightWasOn), { timeout: LIVE_MS })

    // The member switches the plug: the admin sees it flip.
    const plugHere = powerOf(page, SIMULATED.plug)
    const plugThere = powerOf(other, SIMULATED.plug)
    const plugWasOn = (await plugThere.getAttribute('aria-checked')) === 'true'
    await expect(plugHere).toHaveAttribute('aria-checked', String(plugWasOn))
    await plugThere.click()
    await expect(plugHere).toHaveAttribute('aria-checked', String(!plugWasOn), { timeout: LIVE_MS })
    await gates.check('devices after live updates')
  } finally {
    await memberContext.close()
  }
})

test('a command sent through the API shows up live on the device page', async ({ page }) => {
  await page.goto('/devices')
  await page
    .locator('[data-slot="device-tile"]')
    .filter({ hasText: SIMULATED.light })
    .getByRole('link', { name: SIMULATED.light })
    .click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.light })).toBeVisible()
  await waitForLive(page)
  const slider = page.getByRole('slider', { name: t('device.level.label', { name: SIMULATED.light }) })

  // Someone (an automation, another app) sets the level through the API.
  const deviceId = new URL(page.url()).pathname.split('/').pop()!
  const device = (await (await page.request.get(`/api/devices/${deviceId}`)).json()) as {
    endpoints: { id: string }[]
  }
  const response = await page.request.post(`/api/endpoints/${device.endpoints[0]!.id}/commands`, {
    headers: mutationHeaders(),
    data: { capabilityId: 'level', command: 'setLevel', arguments: { level: 37 }, idempotencyKey: null },
  })
  expect(response.status(), `POST commands: ${await response.text()}`).toBe(202)
  await expect(slider).toHaveAttribute('aria-valuenow', '37', { timeout: LIVE_MS + 1_000 })
})
