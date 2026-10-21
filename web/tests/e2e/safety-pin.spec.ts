import type { Locator, Page } from '@playwright/test'
import {
  SIMULATED,
  attributeValue,
  completeSetup,
  createSimulatedHome,
  createUser,
  expect,
  resetApp,
  signIn,
  test,
  testData,
  waitForLive,
} from './fixtures'
import { t } from './i18n'

// Task 1.2-E: the safety PIN. It is set in the profile; unlocking the simulated lock opens the PIN pad, a wrong PIN is
// refused (and nothing is sent to the lock), the right one unlocks it once; a Viewer has no unlock control at all.

const PIN = '4826'
const WRONG_PIN = '1111'

const tile = (page: Page, name: string) => page.locator('[data-slot="device-tile"]').filter({ hasText: name })

/** The PIN dialog of an unlock: it asks the question in the action's words ("Unlock Front door lock?"). */
const unlockDialog = (page: Page) =>
  page.getByRole('dialog', {
    name: t('safety.dialog.question', { action: t('device.commands.unlock'), name: SIMULATED.lock }),
  })

/** The lock page's unlock button ("Unlock…": the PIN comes next). */
const unlockButton = (page: Page) =>
  page.getByRole('button', { name: t('device.lock.unlockAction'), exact: true })

/** Types a PIN on the dialog's pad (its keys, as a person on a phone would) and confirms it ("Unlock"). */
async function enterPin(dialog: Locator, pin: string): Promise<void> {
  for (const digit of pin) await dialog.getByRole('button', { name: digit, exact: true }).click()
  await dialog.getByRole('button', { name: t('device.commands.unlock'), exact: true }).click()
}

async function openLockPage(page: Page): Promise<void> {
  await page.goto('/devices')
  await tile(page, SIMULATED.lock).getByRole('link', { name: SIMULATED.lock }).click()
  await expect(page.getByRole('heading', { level: 1, name: SIMULATED.lock })).toBeVisible()
  await waitForLive(page)
}

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
  await createSimulatedHome(page.request)
})

test('sets the PIN in the profile, then unlocks the lock with it', async ({ page, gates }) => {
  // Profile: no PIN yet → set one.
  await page.goto('/profile')
  const section = page.locator('#safety-pin')
  await expect(section.getByText(t('profile.pin.notSet'))).toBeVisible()
  await section.getByLabel(t('profile.pin.pin'), { exact: true }).fill(PIN)
  await section.getByLabel(t('profile.pin.confirm'), { exact: true }).fill(PIN)
  await section.getByRole('button', { name: t('profile.pin.set') }).click()
  await expect(page.getByText(t('profile.pin.saved'))).toBeVisible()
  await expect(section.getByText(t('profile.pin.isSet'))).toBeVisible()
  await gates.check('profile with a safety PIN')

  // The lock: Unlock → the PIN pad.
  await openLockPage(page)
  const lockState = page.locator('[data-slot="lock-state"]')
  await expect(lockState).toHaveText(t('device.values.lockState.locked'))
  // Locked: the one action is the quiet "Unlock…" (there is nothing to lock).
  await expect(page.getByRole('button', { name: t('device.commands.lock'), exact: true })).toHaveCount(0)
  await unlockButton(page).click()
  const dialog = unlockDialog(page)
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText(t('safety.dialog.enterPin'))
  // Nothing has happened yet: the lock still says locked, waiting for the PIN (not "Unlocking…").
  await expect(lockState).toHaveText(t('device.values.lockState.locked'))
  await expect(page.locator('[data-slot="lock-awaiting-pin"]')).toHaveText(t('device.pending.awaitingPin'))
  await gates.check('safety PIN dialog')

  // A wrong PIN: refused, the dialog stays and the lock stays locked.
  await enterPin(dialog, WRONG_PIN)
  await expect(dialog.locator('[data-slot="pin-error"]')).toHaveText(t('errors.safety.pin_incorrect'))
  await expect(dialog).toBeVisible()
  expect(await attributeValue(page.request, SIMULATED.lock, 'lock', 'lockState')).toBe('locked')

  // The right PIN: the dialog closes and the command goes out (once).
  await enterPin(dialog, PIN)
  await expect(dialog).toBeHidden()
  await expect.poll(() => attributeValue(page.request, SIMULATED.lock, 'lock', 'lockState')).toBe('unlocked')
  await expect(lockState).toHaveText(t('device.values.lockState.unlocked'))

  // Locking again is the safe direction: no PIN.
  await page.getByRole('button', { name: t('device.commands.lock'), exact: true }).click()
  await expect.poll(() => attributeValue(page.request, SIMULATED.lock, 'lock', 'lockState')).toBe('locked')
  await expect(dialog).toBeHidden()
  await expect(lockState).toHaveText(t('device.values.lockState.locked'))
})

test('without a PIN the dialog points to the profile', async ({ page }) => {
  await openLockPage(page)
  await unlockButton(page).click()
  const dialog = unlockDialog(page)
  await enterPin(dialog, PIN)
  await expect(dialog.getByText(t('errors.safety.pin_not_set'))).toBeVisible()
  await dialog.getByRole('link', { name: t('safety.dialog.setPin') }).click()
  await expect(page).toHaveURL(/\/profile#safety-pin$/)
  await expect(dialog).toBeHidden()
  expect(await attributeValue(page.request, SIMULATED.lock, 'lock', 'lockState')).toBe('locked')
})

test('a Viewer sees the lock but has no unlock control', async ({ page, gates }) => {
  await createUser(page.request, testData.viewer, 'Viewer')
  await signIn(page.request, testData.viewer)

  await page.goto('/devices')
  const lock = tile(page, SIMULATED.lock)
  await expect(lock).toBeVisible()
  await expect(lock.locator('[data-slot="tile-state"]')).toContainText(t('device.values.lockState.locked'))
  // No quick action on the tile (only the link to the device page).
  await expect(lock.getByRole('button')).toHaveCount(0)

  await openLockPage(page)
  await expect(page.locator('[data-slot="lock-state"]')).toHaveText(t('device.values.lockState.locked'))
  await expect(unlockButton(page)).toHaveCount(0)
  await expect(page.getByRole('button', { name: t('device.commands.lock'), exact: true })).toHaveCount(0)
  await gates.check('lock page as a Viewer')
})
