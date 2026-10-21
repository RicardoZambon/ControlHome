import type { APIRequestContext } from '@playwright/test'
import {
  SIMULATED,
  advanceClock,
  attributeIdOf,
  completeSetup,
  createSimulatedHome,
  expect,
  resetApp,
  sendCommand,
  test,
} from './fixtures'
import { t } from './i18n'

// Task 1.4-B: a device's History tab. The coffee maker plug (power as a random walk while it is on, energy counted over
// the runtime clock) runs for a few simulated hours — the test moves the server's clock forward
// (`POST /api/_test/clock`), so the Simulated home ticks and the history rollups run — then its page charts the power
// over the last 24 hours and 7 days, and the energy it used as bars.

async function historyPoints(
  request: APIRequestContext,
  attributeId: number,
  from?: string,
): Promise<number> {
  const query = from ? `?from=${encodeURIComponent(from)}` : ''
  const response = await request.get(`/api/attributes/${attributeId}/history${query}`)
  expect(response.status(), await response.text()).toBe(200)
  const series = (await response.json()) as { points: { v: number | null }[] }
  return series.points.filter((point) => point.v !== null).length
}

async function energyRows(request: APIRequestContext, endpointId: string): Promise<number> {
  const response = await request.get(`/api/energy/hourly?endpointId=${endpointId}`)
  expect(response.status(), await response.text()).toBe(200)
  return ((await response.json()) as unknown[]).length
}

test('a device page charts its recorded history and the energy it used', async ({ page, request, gates }) => {
  test.slow()
  await resetApp(request)
  await completeSetup(page.request)
  const home = await createSimulatedHome(page.request)
  const plug = home.plug
  const power = attributeIdOf(plug, 'power', 'power')
  await sendCommand(page.request, plug, 'switch', 'turnOn')

  // Five simulated minutes at a time: the plug reports its power and energy, and the rollups close their buckets.
  await expect
    .poll(
      async () => {
        await advanceClock(page.request, 300)
        return (
          (await historyPoints(page.request, power)) >= 3 &&
          (await energyRows(page.request, plug.endpoints[0]!.id)) >= 1
        )
      },
      { message: 'power statistics and hourly energy to be recorded', timeout: 120_000, intervals: [1_000] },
    )
    .toBe(true)

  await page.goto(`/devices/${plug.id}`)
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.plug })).toBeVisible()
  await page.getByRole('tab', { name: t('devices.tabs.history') }).click()
  await expect(page).toHaveURL(/tab=history/)

  // The power over the last 24 hours: a line drawn from the recorded points.
  const powerCard = page.locator(`[data-slot="attribute-history"][data-attribute-id="${power}"]`)
  const chart = powerCard.locator('[data-slot="history-chart"]')
  await expect(chart).toBeVisible()
  await expect.poll(async () => Number(await chart.getAttribute('data-points'))).toBeGreaterThanOrEqual(3)
  await expect(chart.locator('svg path').first()).toBeAttached()
  await expect(chart.getByRole('img', { name: /last 24 hours/ })).toBeVisible()

  // The energy it used, per hour.
  const bars = page.locator('[data-slot="energy-history"] [data-slot="energy-bars"]')
  await expect.poll(async () => Number(await bars.getAttribute('data-buckets'))).toBeGreaterThanOrEqual(1)
  await expect(page.locator('[data-slot="energy-total"]')).not.toHaveText('—')
  await gates.check('history, last 24 hours')

  // A week: kept in the address, and charted again.
  await page
    .locator('[data-slot="history-range"]')
    .getByRole('button', { name: t('history.rangesShort.7d') })
    .click()
  await expect(page).toHaveURL(/range=7d/)
  await expect(chart.getByRole('img', { name: /last 7 days/ })).toBeVisible()
  await expect.poll(async () => Number(await chart.getAttribute('data-points'))).toBeGreaterThanOrEqual(1)
  await gates.check('history, last 7 days')

  // A reload keeps the tab and the range.
  await page.reload()
  await expect(chart.getByRole('img', { name: /last 7 days/ })).toBeVisible()
})
