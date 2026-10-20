import {
  SIMULATED,
  attributeValue,
  completeSetup,
  createSimulatedHome,
  expect,
  resetApp,
  test,
  waitForLive,
} from './fixtures'
import { t } from './i18n'

// Task 1.2-E: the Simulated connector's basic home, controlled from the device tiles and the device pages. Every change
// is checked twice: on the page at once (optimistic, then confirmed) and on the server (GET /api/devices).

const tile = (page: import('@playwright/test').Page, name: string) =>
  page.locator('[data-slot="device-tile"]').filter({ hasText: name })

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  // Set up through the browser's request context: the browser is signed in as the admin.
  await completeSetup(page.request)
  await createSimulatedHome(page.request)
})

test('switches the light from its tile', async ({ page, gates }) => {
  await page.goto('/devices')
  await waitForLive(page)
  const light = tile(page, SIMULATED.light)
  await expect(light).toBeVisible()
  await gates.check('devices with the simulated home')

  const power = light.getByRole('switch', { name: t('device.switch.label', { name: SIMULATED.light }) })
  const wasOn = (await power.getAttribute('aria-checked')) === 'true'
  await power.click()
  // Optimistic: the switch flips at once, and stays once the device confirms.
  await expect(power).toHaveAttribute('aria-checked', String(!wasOn))
  await expect.poll(() => attributeValue(page.request, SIMULATED.light, 'switch', 'on')).toBe(!wasOn)
  await expect(light.locator('[data-slot="tile-state"]')).toContainText(
    wasOn ? t('device.state.off') : t('device.state.on'),
  )
  await expect(power).toHaveAttribute('aria-checked', String(!wasOn))
})

test('sets the light level and the AC target temperature on their pages', async ({ page, gates }) => {
  await page.goto('/devices')
  await tile(page, SIMULATED.light).getByRole('link', { name: SIMULATED.light }).click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.light })).toBeVisible()
  await waitForLive(page)

  // Brightness: a preset, then the keyboard on the slider.
  await page.getByRole('button', { name: '50 %' }).click()
  await expect.poll(() => attributeValue(page.request, SIMULATED.light, 'level', 'level')).toBe(50)
  await expect(page.getByRole('button', { name: '50 %' })).toHaveAttribute('aria-pressed', 'true')
  const slider = page.getByRole('slider', { name: t('device.level.label', { name: SIMULATED.light }) })
  await slider.focus()
  await page.keyboard.press('ArrowRight')
  await page.keyboard.press('ArrowRight')
  await expect.poll(() => attributeValue(page.request, SIMULATED.light, 'level', 'level')).toBe(52)
  await gates.check('light page')

  // The AC: two steps up on the dial's + button (sent once, after the taps).
  await page.goto('/devices')
  await tile(page, SIMULATED.ac).getByRole('link', { name: SIMULATED.ac }).click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.ac })).toBeVisible()
  await waitForLive(page)
  const before = (await attributeValue(
    page.request,
    SIMULATED.ac,
    'thermostat',
    'targetTemperature',
  )) as number
  const dial = page.getByRole('slider', { name: t('device.thermostat.target', { name: SIMULATED.ac }) })
  await expect(dial).toHaveAttribute('aria-valuenow', String(before))
  const raise = page.getByRole('button', { name: t('device.thermostat.raise') })
  await raise.click()
  await raise.click()
  await expect(dial).toHaveAttribute('aria-valuenow', String(before + 1))
  await expect
    .poll(() => attributeValue(page.request, SIMULATED.ac, 'thermostat', 'targetTemperature'))
    .toBe(before + 1)

  // Mode: heat (its range tops at 26 °C on this AC). The modes are a segmented choice of toggle buttons.
  const modes = page.getByRole('group', { name: t('device.attributes.mode') })
  const heat = modes.getByRole('button', { name: t('device.values.mode.heat'), exact: true })
  await heat.click()
  await expect(heat).toHaveAttribute('aria-pressed', 'true')
  await expect.poll(() => attributeValue(page.request, SIMULATED.ac, 'thermostat', 'mode')).toBe('heat')
  await expect(dial).toHaveAttribute('aria-valuemax', '26')
  await gates.check('AC page')
})
