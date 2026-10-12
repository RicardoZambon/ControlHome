import type { Page } from '@playwright/test'
import {
  closeNavigation,
  completeSetup,
  expect,
  openNavigation,
  profileSaved,
  resetApp,
  signInWithForm,
  signOutFromMenu,
  test,
  testData,
} from './fixtures'
import { t, tr } from './i18n'

const { admin } = testData

test.beforeEach(async ({ page }) => {
  await resetApp(page.request)
  // Through the page's request context: the browser is signed in as the admin.
  await completeSetup(page.request)
})

/** Forgets this browser's saved theme and language, as on another device, and reloads. */
async function forgetLocalPreferences(page: Page): Promise<void> {
  await page.evaluate(() => localStorage.clear())
  await page.reload()
}

test('the dark theme survives a reload and follows the account to another device', async ({
  page,
  gates,
}) => {
  const html = page.locator('html')
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(html).not.toHaveClass(/\bdark\b/)

  await page.getByRole('button', { name: t('shell.chooseTheme') }).click()
  const saved = profileSaved(page)
  await page.getByRole('menuitemradio', { name: t('theme.dark') }).click()
  expect((await saved).status()).toBe(200)
  await expect(html).toHaveClass(/\bdark\b/)
  await gates.check('dashboard, dark')

  await page.reload()
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(html).toHaveClass(/\bdark\b/)

  await signOutFromMenu(page, admin.displayName)
  await forgetLocalPreferences(page)
  await expect(html).not.toHaveClass(/\bdark\b/)

  await signInWithForm(page, admin)
  await expect(page).toHaveURL(/\/$/)
  await expect(html).toHaveClass(/\bdark\b/)
  await gates.check('dashboard after signing in again, dark')
})

test('Portuguese survives a reload and follows the account to another device', async ({ page, gates }) => {
  const html = page.locator('html')
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(html).toHaveAttribute('lang', 'en')

  await page.getByRole('button', { name: t('shell.chooseLanguage') }).click()
  const saved = profileSaved(page)
  await page.getByRole('menuitemradio', { name: t('locales.ptBR') }).click()
  expect((await saved).status()).toBe(200)
  await expect(html).toHaveAttribute('lang', 'pt-BR')
  await expect(page.getByRole('button', { name: tr('pt-BR', 'shell.chooseLanguage') })).toBeVisible()
  await gates.check('dashboard in Portuguese')

  await page.reload()
  await expect(html).toHaveAttribute('lang', 'pt-BR')
  const navigation = await openNavigation(page, 'pt-BR')
  await expect(navigation.getByRole('link', { name: tr('pt-BR', 'users.nav'), exact: true })).toBeVisible()
  await closeNavigation(page, 'pt-BR')

  await signOutFromMenu(page, admin.displayName, 'pt-BR')
  await forgetLocalPreferences(page)
  await expect(html).toHaveAttribute('lang', 'en')
  await expect(page.getByRole('heading', { name: t('auth.login.heading') })).toBeVisible()

  await signInWithForm(page, admin)
  await expect(page).toHaveURL(/\/$/)
  await expect(html).toHaveAttribute('lang', 'pt-BR')
  await expect(
    page.getByRole('button', { name: tr('pt-BR', 'shell.userMenu', { name: admin.displayName }) }),
  ).toBeVisible()
  await gates.check('dashboard after signing in again, Portuguese')
})
