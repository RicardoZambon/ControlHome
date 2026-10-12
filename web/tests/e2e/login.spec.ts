import { completeSetup, expect, resetApp, signInWithForm, signOutFromMenu, test, testData } from './fixtures'
import { t } from './i18n'

const { admin } = testData

/** The sign-in page, asked to come back to `path` afterwards. */
const signInFor = (path: string) => (url: URL) =>
  url.pathname === '/login' && url.searchParams.get('redirect') === path

test.beforeEach(async ({ request }) => {
  await resetApp(request)
  // Through Playwright's own request context: the browser stays signed out.
  await completeSetup(request)
})

test('signs in and out', async ({ page, gates }) => {
  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('heading', { name: t('auth.login.heading') })).toBeVisible()
  await gates.check('/login')

  await signInWithForm(page, admin)
  await expect(page).toHaveURL(/\/$/)
  await expect(
    page.getByRole('button', { name: t('shell.userMenu', { name: admin.displayName }) }),
  ).toBeVisible()
  await gates.check('dashboard')

  await signOutFromMenu(page, admin.displayName)
  await expect(page.getByText(t('shell.loggedOut'))).toBeVisible()

  // The session ended on the server too: a page that needs it asks to sign in again.
  await page.goto('/profile')
  await expect(page).toHaveURL(signInFor('/profile'))
})

test('a wrong password shows a message and keeps the visitor signed out', async ({ page, gates }) => {
  await page.goto('/login')
  await signInWithForm(page, { ...admin, password: 'not the right password' })

  await expect(page.getByRole('alert')).toContainText(t('errors.auth.invalid_credentials'))
  await expect(page).toHaveURL(/\/login$/)
  await gates.check('/login with an error')

  // The right password still works (one failure is far from a lockout).
  await signInWithForm(page, admin)
  await expect(page).toHaveURL(/\/$/)
})

test('returns to the requested page after signing in', async ({ page }) => {
  await page.goto('/users')
  await expect(page).toHaveURL(signInFor('/users'))

  await signInWithForm(page, admin)

  await expect(page).toHaveURL(/\/users$/)
  await expect(page.getByRole('heading', { level: 1, name: t('users.title') })).toBeVisible()
})
