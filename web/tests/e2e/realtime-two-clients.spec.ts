import type { Page, WebSocketRoute } from '@playwright/test'
import {
  SIMULATED,
  attributeValue,
  completeSetup,
  createSimulatedHome,
  createUser,
  expect,
  resetApp,
  sendCommand,
  signIn,
  test,
  testData,
  waitForLive,
} from './fixtures'
import { t } from './i18n'

// Task 1.2-E: live updates over SignalR. Two people (two browser contexts) look at the same devices; what one changes
// appears on the other's screen within 2 s, without a reload. A change made outside the browser (the API) arrives the
// same way, and one made while the browser was offline shows up as soon as it reconnects (the state is fetched again).

const LIVE_MS = 2_000

const tileOf = (page: Page, name: string) =>
  page.locator('[data-slot="device-tile"]').filter({ hasText: name })

const powerOf = (page: Page, name: string) =>
  tileOf(page, name).getByRole('switch', { name: t('device.switch.label', { name }) })

let home: Awaited<ReturnType<typeof createSimulatedHome>>

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
  home = await createSimulatedHome(page.request)
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
  await tileOf(page, SIMULATED.light).getByRole('link', { name: SIMULATED.light }).click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.light })).toBeVisible()
  await waitForLive(page)
  const slider = page.getByRole('slider', { name: t('device.level.label', { name: SIMULATED.light }) })

  // Someone (an automation, another app) sets the level through the API.
  await sendCommand(page.request, home.light, 'level', 'setLevel', { level: 37 })
  await expect(slider).toHaveAttribute('aria-valuenow', '37', { timeout: LIVE_MS + 1_000 })
})

test('after the connection drops, the page catches up on what it missed', async ({
  page,
  request,
  gates,
}) => {
  // Changes "elsewhere" go through Playwright's own request context, which the browser's offline switch does not touch.
  await signIn(request, testData.admin)
  // Chromium's offline mode fails new requests but leaves WebSockets that are already open alone, so the hub's socket
  // passes through a route that can end it the way a lost network would.
  const hubSockets: { client: WebSocketRoute; server: WebSocketRoute }[] = []
  await page.routeWebSocket(/\/hubs\/home/, (client) => {
    hubSockets.push({ client, server: client.connectToServer() })
  })
  await page.goto('/devices')
  await waitForLive(page)
  const light = powerOf(page, SIMULATED.light)
  const plug = powerOf(page, SIMULATED.plug)
  const lightWasOn = (await light.getAttribute('aria-checked')) === 'true'
  const plugWasOn = (await plug.getAttribute('aria-checked')) === 'true'

  // The network goes away: the hub connection drops, and after 3 s the banner says the states may be stale. Meanwhile
  // the browser reports the reconnect attempts that fail.
  gates.expectConsoleErrors(/ERR_INTERNET_DISCONNECTED|WebSocket connection to .+\/hubs\/home/)
  await page.context().setOffline(true)
  expect(hubSockets.length, 'the page connected to the hub').toBeGreaterThan(0)
  for (const { client, server } of hubSockets.splice(0)) {
    await client.close({ code: 4000, reason: 'Network lost' })
    await server.close()
  }
  await expect(page.locator('[data-realtime-status="reconnecting"]')).toBeAttached()
  const banner = page.locator('[data-slot="realtime-banner"]')
  await expect(banner).toContainText(t('live.reconnecting'))
  await gates.check('devices while reconnecting')

  // While it is away, the light and the plug are switched elsewhere: their live updates never reach this page.
  await sendCommand(request, home.light, 'switch', lightWasOn ? 'turnOff' : 'turnOn')
  await sendCommand(request, home.plug, 'switch', plugWasOn ? 'turnOff' : 'turnOn')
  await expect.poll(() => attributeValue(request, SIMULATED.light, 'switch', 'on')).toBe(!lightWasOn)
  await expect.poll(() => attributeValue(request, SIMULATED.plug, 'switch', 'on')).toBe(!plugWasOn)
  await expect(light).toHaveAttribute('aria-checked', String(lightWasOn))
  await expect(plug).toHaveAttribute('aria-checked', String(plugWasOn))

  // Back online: the connection comes back by itself, the page fetches the whole state again and shows what it missed.
  await page.context().setOffline(false)
  await waitForLive(page)
  await expect(light).toHaveAttribute('aria-checked', String(!lightWasOn))
  await expect(plug).toHaveAttribute('aria-checked', String(!plugWasOn))
  await expect(banner).toBeHidden()
  await expect(page.getByRole('status').filter({ hasText: t('live.back') })).toBeVisible()

  // Live again: the next change arrives without a reload, and nothing is left pending.
  await sendCommand(request, home.light, 'switch', 'toggle')
  await expect(light).toHaveAttribute('aria-checked', String(lightWasOn), { timeout: LIVE_MS })
  for (const name of [SIMULATED.light, SIMULATED.plug])
    await expect(tileOf(page, name).locator('[data-slot="tile-state"]')).not.toHaveAttribute('data-pending')
  await gates.check('devices after reconnecting')
})
