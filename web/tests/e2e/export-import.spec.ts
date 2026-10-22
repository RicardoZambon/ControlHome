import { statSync, writeFileSync } from 'node:fs'
import type { APIRequestContext } from '@playwright/test'
import {
  SIMULATED,
  completeSetup,
  createFloor,
  dismissToasts,
  expect,
  listDevices,
  listPlacements,
  mutationHeaders,
  placeEndpoint,
  resetApp,
  test,
} from './fixtures'
import { t } from './i18n'

// Task 1.3-C: Settings → Backup. A home is downloaded as a zip (with its integrations' credentials too when a
// passphrase protects them), then restored into a new, empty home: floors, rooms, the integration, its devices and their
// places on the plan come back. A home that already has things in it can't take a backup.

const PASSPHRASE = 'correct horse battery staple'

type FlowStep = { flowId: string; type: string }

/**
 * Adds the Simulated basic home through its setup flow (`POST /api/integrations/flows`), so the integration has a
 * credential (the hub's token) for the backup to carry: the settings, then the "press the button" step, polled until it
 * is done (it presses itself after 2 s of the runtime clock).
 */
async function addSimulatedHomeWithFlow(request: APIRequestContext): Promise<void> {
  const answer = async (url: string, data: unknown): Promise<FlowStep> => {
    const response = await request.post(url, { headers: mutationHeaders(), data })
    expect(response.status(), `POST ${url}: ${await response.text()}`).toBe(200)
    return (await response.json()) as FlowStep
  }
  const form = await answer('/api/integrations/flows', { connectorKey: 'simulated' })
  expect(form.type).toBe('form')
  const continueUrl = `/api/integrations/flows/${form.flowId}`
  let step = await answer(continueUrl, { input: { preset: 'basic', seed: 42, simulateReauth: false } })
  await expect
    .poll(
      async () => {
        if (step.type === 'progress') step = await answer(continueUrl, { input: {} })
        return step.type
      },
      { timeout: 20_000, intervals: [1_000] },
    )
    .toBe('done')
  await expect.poll(async () => (await listDevices(request)).length, { timeout: 30_000 }).toBe(5)
}

test('a backup with its credentials is restored into a new, empty home', async ({
  page,
  request,
  gates,
}, testInfo) => {
  await resetApp(request)
  await completeSetup(page.request)
  const floor = await createFloor(page.request)
  await addSimulatedHomeWithFlow(page.request)
  const light = (await listDevices(page.request)).find((device) => device.name === SIMULATED.light)!
  await placeEndpoint(page.request, light, floor.id, 500, 400)

  await page.goto('/settings/backup')
  await expect(page.getByRole('heading', { level: 1, name: t('settings.backup.title') })).toBeVisible()
  // This home has floors and devices: nothing can be restored into it, so the card only says how restoring works.
  await expect(page.locator('[data-slot="backup-not-empty"]')).toBeVisible()
  await expect(page.locator('[data-slot="backup-restore"]')).toHaveCount(0)
  await expect(page.locator('[data-slot="backup-file"]')).toHaveCount(0)
  await gates.check('backup page')

  // The automatic backups (the server's data folder outlives the app's reset: earlier runs may have left some): one
  // more written now, listed to download.
  const scheduled = page.locator('[data-slot="backup-scheduled"]')
  await expect(scheduled).toHaveAttribute('data-loaded', 'true')
  const files = scheduled.locator('[data-slot="backup-files"] li')
  const before = await files.count()
  await page.locator('[data-slot="backup-now"]').click()
  await expect(files).toHaveCount(Math.min(before + 1, 7))
  await expect(scheduled.locator('[data-slot="backup-last"]')).toBeVisible()
  const kept = page.waitForEvent('download')
  await files.first().getByRole('button').click()
  expect((await kept).suggestedFilename()).toMatch(/^controlhome-backup-\d{8}-\d{6}\.zip$/)
  await dismissToasts(page)

  // With the credentials: the passphrase is checked before anything is sent.
  const exportForm = page.locator('[data-slot="backup-export"]')
  await page.locator('[data-slot="backup-secrets"]').click()
  await exportForm.getByLabel(t('settings.backup.export.passphrase'), { exact: true }).fill('too short')
  await exportForm.getByLabel(t('settings.backup.export.confirm'), { exact: true }).fill('too short')
  await page.locator('[data-slot="backup-download"]').click()
  await expect(page.getByRole('alert')).toContainText(
    t('settings.backup.export.problems.tooShort', { min: 12 }),
  )
  await exportForm.getByLabel(t('settings.backup.export.passphrase'), { exact: true }).fill(PASSPHRASE)
  await exportForm.getByLabel(t('settings.backup.export.confirm'), { exact: true }).fill(PASSPHRASE)
  const downloading = page.waitForEvent('download')
  await page.locator('[data-slot="backup-download"]').click()
  const download = await downloading
  expect(download.suggestedFilename()).toMatch(/\.zip$/)
  const file = testInfo.outputPath(download.suggestedFilename())
  await download.saveAs(file)
  expect(statSync(file).size).toBeGreaterThan(1_000)
  await expect(page.getByText(t('settings.backup.export.doneWithSecrets'))).toBeVisible()

  // A new home: the admin restores the backup, credentials included.
  await resetApp(request)
  await completeSetup(page.request)
  await page.goto('/settings/backup')
  await expect(page.locator('[data-slot="backup-import"]')).toBeVisible()
  await expect(page.locator('[data-slot="backup-not-empty"]')).toHaveCount(0)
  await page.locator('[data-slot="backup-file"]').setInputFiles(file)
  // No passphrase asked before it turns out to be needed: the backup holds credentials, so the server asks for it.
  const passphraseField = page
    .locator('[data-slot="backup-import"]')
    .getByLabel(t('settings.backup.import.passphrase'), { exact: true })
  await expect(passphraseField).toHaveCount(0)
  await page.locator('[data-slot="backup-restore"]').click()
  await expect(page.getByRole('alert')).toContainText(t('errors.backup.passphrase_required'))
  await passphraseField.fill(PASSPHRASE)
  await page.locator('[data-slot="backup-restore"]').click()

  const result = page.locator('[data-slot="backup-result"]')
  await expect(result).toBeVisible({ timeout: 30_000 })
  await expect(result).toContainText(t('settings.backup.import.secretsBack'))
  const count = (key: string) =>
    result
      .locator('div')
      .filter({ hasText: t(`settings.backup.import.counts.${key}`) })
      .locator('dd')
  await expect(count('floors')).toHaveText('1')
  await expect(count('rooms')).toHaveText('3')
  await expect(count('integrations')).toHaveText('1')
  await expect(count('devices')).toHaveText('5')
  await expect(count('placements')).toHaveText('1')
  await gates.check('backup restored')

  // The server has it all again, the light still on its spot.
  expect(await listDevices(page.request)).toHaveLength(5)
  const placements = await listPlacements(page.request)
  expect(placements).toHaveLength(1)
  expect(placements[0]).toMatchObject({ xCm: 500, yCm: 400 })

  // The restored integration runs again with its credential: its devices come back online, and the light is on the
  // floor's plan.
  await expect
    .poll(
      async () => {
        const devices = await listDevices(page.request)
        return devices.length === 5 && devices.every((device) => device.availability === 'Online')
      },
      { message: 'the restored devices to come back online', timeout: 30_000 },
    )
    .toBe(true)
  await page.goto(`/floors/${placements[0]!.floorId}`)
  await expect(page.locator('[data-slot="placed-device"]')).toHaveCount(1)
  await expect(page.getByRole('button', { name: new RegExp(`^${SIMULATED.light}`) })).toBeVisible()
})

test('a file that is not a backup is refused before it is sent', async ({ page, request }, testInfo) => {
  await resetApp(request)
  await completeSetup(page.request)
  await page.goto('/settings/backup')
  const notes = testInfo.outputPath('notes.txt')
  writeFileSync(notes, 'not a backup')
  await page.locator('[data-slot="backup-file"]').setInputFiles(notes)
  await expect(page.getByRole('alert')).toContainText(t('settings.backup.import.notZip'))
  await expect(page.locator('[data-slot="backup-restore"]')).toBeDisabled()
})
