import { SETUP_CODE, expect, resetApp, test, testData } from './fixtures'
import { t } from './i18n'

test.beforeEach(async ({ request }) => {
  await resetApp(request)
})

test('every page leads to the setup wizard until the home is set up', async ({ page, gates }) => {
  await page.goto('/')
  await expect(page).toHaveURL(/\/setup$/)
  await expect(page.getByRole('heading', { name: t('setup.heading') })).toBeVisible()
  await gates.check('/setup')

  for (const path of ['/login', '/users', '/profile']) {
    await page.goto(path)
    await expect(page, `${path} while setup is incomplete`).toHaveURL(/\/setup$/)
  }
})

test('a wrong setup code is rejected; with the right one the admin lands on the dashboard', async ({
  page,
  gates,
}) => {
  const { admin, home } = testData
  const stepTitle = (step: string) => page.getByRole('heading', { name: t(`setup.steps.${step}.title`) })
  const next = () => page.getByRole('button', { name: t('common.continue') }).click()

  await page.goto('/setup')

  // Code: checked by the server only when the wizard finishes.
  await expect(stepTitle('code')).toBeVisible()
  await page.getByLabel(t('setup.code.label')).fill('WRNG-CODE-0000')
  await next()

  await expect(stepTitle('admin')).toBeVisible()
  await page.getByLabel(t('setup.admin.displayName')).fill(admin.displayName)
  await page.getByLabel(t('setup.admin.userName')).fill(admin.userName)
  await page.getByLabel(t('setup.admin.password'), { exact: true }).fill(admin.password)
  await page.getByLabel(t('setup.admin.confirmPassword')).fill(admin.password)
  await gates.check('setup: administrator step')
  await next()

  await expect(stepTitle('home')).toBeVisible()
  await page.getByLabel(t('setup.home.name')).fill(home.name)
  await gates.check('setup: home step')
  await next()

  await expect(stepTitle('review')).toBeVisible()
  await expect(page.getByText(home.name)).toBeVisible()
  await gates.check('setup: review step')
  await page.getByRole('button', { name: t('setup.review.finish') }).click()

  // Back on the first step, with the server's message on the field.
  await expect(stepTitle('code')).toBeVisible()
  await expect(page.getByText(t('errors.setup.invalid_code'))).toBeVisible()
  await gates.check('setup: wrong code')

  // The other steps kept what was typed.
  await page.getByLabel(t('setup.code.label')).fill(SETUP_CODE)
  await next()
  await expect(stepTitle('admin')).toBeVisible()
  await expect(page.getByLabel(t('setup.admin.userName'))).toHaveValue(admin.userName)
  await next()
  await expect(stepTitle('home')).toBeVisible()
  await next()
  await expect(stepTitle('review')).toBeVisible()
  await page.getByRole('button', { name: t('setup.review.finish') }).click()

  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('heading', { level: 1 })).toContainText(admin.displayName.split(' ')[0]!)
  await expect(page.getByText(t('setup.done', { name: admin.displayName }))).toBeVisible()
  await expect(
    page.getByRole('button', { name: t('shell.userMenu', { name: admin.displayName }) }),
  ).toBeVisible()

  // Setup can't be opened again.
  await page.goto('/setup')
  await expect(page).toHaveURL(/\/$/)
})

test('the demo home loads with the setup: floors, rooms and devices on the plan', async ({ page, gates }) => {
  const { admin, home } = testData
  const stepTitle = (step: string) => page.getByRole('heading', { name: t(`setup.steps.${step}.title`) })
  const next = () => page.getByRole('button', { name: t('common.continue') }).click()

  await page.goto('/setup')
  await page.getByLabel(t('setup.code.label')).fill(SETUP_CODE)
  await next()
  await page.getByLabel(t('setup.admin.displayName')).fill(admin.displayName)
  await page.getByLabel(t('setup.admin.userName')).fill(admin.userName)
  await page.getByLabel(t('setup.admin.password'), { exact: true }).fill(admin.password)
  await page.getByLabel(t('setup.admin.confirmPassword')).fill(admin.password)
  await next()
  await page.getByLabel(t('setup.home.name')).fill(home.name)
  await next()

  // The review offers the demo home.
  await expect(stepTitle('review')).toBeVisible()
  const demo = page.getByRole('switch', { name: t('setup.review.demoTitle') })
  await expect(demo).toHaveAttribute('aria-checked', 'false')
  await demo.click()
  await expect(demo).toHaveAttribute('aria-checked', 'true')
  await gates.check('setup: review with the demo home')
  await page.getByRole('button', { name: t('setup.review.finish') }).click()

  // The dashboard: the first floor with its devices on it, every room, the favorites, the integration's health.
  await expect(page).toHaveURL(/\/$/)
  const plan = page.locator('[data-slot="dashboard-plan"]')
  await expect(plan).toBeVisible()
  await expect.poll(() => plan.locator('[data-slot="placed-device"]').count()).toBeGreaterThan(0)
  await expect(page.locator('[data-slot="room-card"]')).toHaveCount(8)
  await expect(
    page.locator('[data-slot="dashboard-favorites"] [data-slot="device-tile"]').first(),
  ).toBeVisible()
  await expect(page.locator('[data-slot="integration-chip"]')).toHaveCount(1)
  // A full home: no first steps.
  await expect(page.locator('[data-slot="dashboard-start"]')).toHaveCount(0)
  await gates.check('dashboard with the demo home')
})
