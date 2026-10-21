import { statSync, writeFileSync } from 'node:fs'
import {
  completeSetup,
  createFloor,
  createSimulatedHome,
  expect,
  listDevices,
  listPlacements,
  placeEndpoint,
  resetApp,
  test,
} from './fixtures'
import { t } from './i18n'

// Task 1.3-C: Settings → Backup. A home is downloaded as a zip (with its integrations' credentials too when a
// passphrase protects them), then restored into a new, empty home: floors, rooms, the integration, its devices and their
// places on the plan come back. A home that already has things in it can't take a backup.

const PASSPHRASE = 'correct horse battery staple'

test('a backup with its credentials is restored into a new, empty home', async ({
  page,
  request,
  gates,
}, testInfo) => {
  await resetApp(request)
  await completeSetup(page.request)
  const floor = await createFloor(page.request)
  const home = await createSimulatedHome(page.request)
  await placeEndpoint(page.request, home.light, floor.id, 500, 400)

  await page.goto('/settings/backup')
  await expect(page.getByRole('heading', { level: 1, name: t('settings.backup.title') })).toBeVisible()
  // This home has floors and devices: nothing can be restored into it.
  await expect(page.locator('[data-slot="backup-not-empty"]')).toBeVisible()
  await expect(page.locator('[data-slot="backup-restore"]')).toBeDisabled()
  await gates.check('backup page')

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
  await page
    .locator('[data-slot="backup-import"]')
    .getByLabel(t('settings.backup.import.passphrase'), { exact: true })
    .fill(PASSPHRASE)
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
