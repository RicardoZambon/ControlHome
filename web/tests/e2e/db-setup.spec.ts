import { DB_SETUP } from './env'
import { SETUP_CODE, expect, test, testData } from './fixtures'
import { t } from './i18n'

// Pre-database setup (Task 0.4-B) against an app started WITHOUT a database connection: `scripts/e2e.ps1 -PreDb`, or the
// second compose run in CI. It configures the server's database for good, so it runs once (desktop project; the database
// step is also checked at 375 px) and only when E2E_DB_* say which SQL Server the app should connect to.
test.skip(
  !DB_SETUP,
  'Pre-database setup only: set E2E_DB_HOST, E2E_DB_PORT, E2E_DB_USER, E2E_DB_PASSWORD and E2E_DB_NAME.',
)

test('the database is connected from the setup wizard, then the home is set up', async ({
  page,
  request,
  gates,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'Configures the server once: desktop project only.')
  test.setTimeout(5 * 60_000)
  const db = DB_SETUP!
  const { admin, home } = testData
  const stepTitle = (step: string) => page.getByRole('heading', { name: t(`setup.steps.${step}.title`) })
  const next = () => page.getByRole('button', { name: t('common.continue') }).click()
  const connect = () => page.getByRole('button', { name: t('setup.database.connect') }).click()

  const before = await request.get('/api/setup/status')
  expect(await before.json(), 'the app must start without a database connection').toMatchObject({
    databaseConfigured: false,
    databaseSource: 'none',
    databaseState: 'NotConfigured',
  })

  await page.goto('/')
  await expect(page).toHaveURL(/\/setup$/)
  const progress = page.getByRole('navigation', { name: t('setup.progress') })
  await expect(progress.getByRole('listitem')).toHaveCount(5)
  await expect(progress.getByRole('listitem').nth(1)).toContainText(t('setup.steps.database.label'))

  // The setup code is typed once, on the first step; the database step sends it along.
  await page.getByLabel(t('setup.code.label')).fill(SETUP_CODE)
  await next()
  await expect(stepTitle('database')).toBeVisible()
  await expect(page.getByLabel(t('setup.database.port'), { exact: true })).toHaveValue('1433')
  await expect(page.getByLabel(t('setup.database.name'), { exact: true })).toHaveValue('ControlHome')
  await expect(page.getByRole('switch', { name: t('setup.database.trust') })).toHaveAttribute(
    'aria-checked',
    'true',
  )
  await gates.check('setup: database step')
  await page.setViewportSize({ width: 375, height: 812 })
  await gates.check('setup: database step at 375 px')
  await page.setViewportSize({ width: 1440, height: 900 })

  await page.getByLabel(t('setup.database.host'), { exact: true }).fill(db.host)
  await page.getByLabel(t('setup.database.port'), { exact: true }).fill(String(db.port))
  await page.getByLabel(t('setup.database.name'), { exact: true }).fill(db.database)
  await page.getByLabel(t('setup.database.userName'), { exact: true }).fill(db.user)

  // A wrong password: the reason is explained, and nothing is saved.
  await page.getByLabel(t('setup.database.password'), { exact: true }).fill('Not-The-Password-42')
  await connect()
  const refused = page.getByRole('alert')
  await expect(refused).toContainText(t('setup.database.connectFailed'))
  await expect(refused).toContainText(t('setup.database.reasons.login_failed'))
  await gates.check('setup: database login refused')
  expect((await (await request.get('/api/setup/status')).json()).databaseConfigured).toBe(false)

  // The right one: saved, then the server creates and migrates the database and the wizard moves on by itself.
  await page.getByLabel(t('setup.database.password'), { exact: true }).fill(db.password)
  await connect()
  await expect(stepTitle('admin')).toBeVisible({ timeout: 180_000 })
  await expect(page.getByText(t('setup.database.ready'))).toBeVisible()
  expect(await (await request.get('/api/setup/status')).json()).toMatchObject({
    databaseConfigured: true,
    databaseSource: 'file',
    databaseState: 'Ready',
    setupCompleted: false,
  })

  // Back shows the database as connected (it can't be changed any more), then on to the administrator.
  await page.getByRole('button', { name: t('common.back') }).click()
  await expect(page.getByText(t('setup.database.connected.title'))).toBeVisible()
  await next()

  await expect(stepTitle('admin')).toBeVisible()
  await page.getByLabel(t('setup.admin.displayName')).fill(admin.displayName)
  await page.getByLabel(t('setup.admin.userName')).fill(admin.userName)
  await page.getByLabel(t('setup.admin.password'), { exact: true }).fill(admin.password)
  await page.getByLabel(t('setup.admin.confirmPassword')).fill(admin.password)
  await next()

  await expect(stepTitle('home')).toBeVisible()
  await page.getByLabel(t('setup.home.name')).fill(home.name)
  await next()

  await expect(stepTitle('review')).toBeVisible()
  await page.getByRole('button', { name: t('setup.review.finish') }).click()

  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByText(t('setup.done', { name: admin.displayName }))).toBeVisible()
  expect(await (await request.get('/api/setup/status')).json()).toMatchObject({
    setupCompleted: true,
    databaseSource: 'file',
  })
})
