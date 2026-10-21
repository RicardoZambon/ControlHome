import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import type { APIRequestContext, Locator, Page } from '@playwright/test'
import {
  SETUP_CODE,
  attributeIdOf,
  attributeValue,
  completeSetup,
  createSimulatedHome,
  createUser,
  dismissToasts,
  expect,
  mutationHeaders,
  placeEndpoint,
  planPoint,
  resetApp,
  savePreferences,
  sendCommand,
  signIn,
  tapPlan,
  test,
  testData,
  waitForLive,
  type SimulatedDevice,
} from './fixtures'
import { LOCALES, t, tr, type Locale } from './i18n'

// `npm run screenshots` (playwright.screenshots.config.ts): every route × {1440, 375} px × {light, dark} × {en, pt-BR}
// into ./screenshots/<route>-<width>-<theme>-<locale>.png (gitignored), for visual review. The global gates run on every
// page too, so each theme and language is checked for contrast, overflow and missing translations.

const outputDir = fileURLToPath(new URL('./screenshots/', import.meta.url))
const WIDTHS = [1440, 375] as const
const THEMES = ['light', 'dark'] as const

/**
 * The whole page. On phones the viewport is first stretched to the page's height, so fixed parts (the bottom tab bar)
 * sit where they belong instead of where the first screen ended, as a stitched full-page capture would put them.
 */
async function screenshotWholePage(page: Page, path: string): Promise<void> {
  const viewport = page.viewportSize()
  if (!viewport || viewport.width >= 768) {
    await page.screenshot({ path, fullPage: true })
    return
  }
  const height = await page.evaluate(() => document.documentElement.scrollHeight)
  await page.setViewportSize({ width: viewport.width, height: Math.max(height, viewport.height) })
  await page.screenshot({ path })
  await page.setViewportSize(viewport)
}

/**
 * Pre-database setup mode, simulated in the browser (the app under test has its database): `GET /api/setup/status`
 * answers "no database connection", so the wizard asks for one right after the setup code. Undo with `page.unrouteAll()`.
 */
async function simulateNoDatabase(page: Page): Promise<void> {
  await page.route('**/api/setup/status', (route) =>
    route.fulfill({
      json: {
        setupCompleted: false,
        databaseState: 'NotConfigured',
        databaseConfigured: false,
        databaseSource: 'none',
        databaseError: null,
        version: 'screenshots',
      },
    }),
  )
}

/** Opens the wizard's database step (status simulated by `simulateNoDatabase`). */
async function openDatabaseStep(page: Page, locale: Locale): Promise<void> {
  await page.goto('/setup')
  await page.getByLabel(tr(locale, 'setup.code.label')).fill(SETUP_CODE)
  await page.getByRole('button', { name: tr(locale, 'common.continue') }).click()
  await expect(page.getByRole('heading', { name: tr(locale, 'setup.steps.database.title') })).toBeVisible()
}

interface Route {
  path: string
  name: string
  /** Something that is only there once the page has its data. */
  ready: (page: Page, locale: Locale) => Locator
}

const anonymousRoutes: Route[] = [
  {
    path: '/setup',
    name: 'setup',
    ready: (page, locale) => page.getByRole('heading', { name: tr(locale, 'setup.steps.code.title') }),
  },
  {
    path: '/login',
    name: 'login',
    ready: (page, locale) => page.getByRole('heading', { name: tr(locale, 'auth.login.heading') }),
  },
]

const signedInRoutes: Route[] = [
  {
    path: '/',
    name: 'dashboard',
    // Captured once the simulated home is in: the summary, and the devices (the favorite light) have loaded too.
    ready: (page) => page.locator('[data-slot="dashboard-favorites"] [data-slot="device-tile"]'),
  },
  {
    path: '/users',
    name: 'users',
    ready: (page) => page.getByText(testData.member.userName, { exact: true }).filter({ visible: true }),
  },
  {
    path: '/profile',
    name: 'profile',
    ready: (page, locale) => page.getByRole('heading', { level: 1, name: tr(locale, 'profile.title') }),
  },
  {
    path: '/dev/ui-kit',
    name: 'dev-ui-kit',
    ready: (page, locale) => page.getByRole('heading', { level: 1, name: tr(locale, 'system.uiKit.title') }),
  },
]

const PLAN_PNG = readFileSync(new URL('./fixtures/plan.png', import.meta.url))

/**
 * A floor traced over fixtures/plan.png (800 × 600 px at 2.5 cm/px = the 2000 × 1500 cm floor, walls at 100 cm from
 * its edges) with three rooms named in `locale`, so the floor pages and the dashboard have something to show.
 */
async function seedFloor(request: APIRequestContext, locale: Locale): Promise<{ id: number; name: string }> {
  const name = tr(locale, 'floors.level.ground')
  const created = await request.post('/api/floors', {
    headers: mutationHeaders(),
    data: { name, level: 0, widthCm: 2000, heightCm: 1500 },
  })
  expect(created.status(), await created.text()).toBe(201)
  let floor = (await created.json()) as { id: number; rowVersion: string }

  const upload = await request.post('/api/files', {
    headers: mutationHeaders(),
    multipart: {
      file: { name: 'plan.png', mimeType: 'image/png', buffer: PLAN_PNG },
      purpose: 'floor-background',
    },
  })
  expect(upload.status(), await upload.text()).toBe(201)
  const file = (await upload.json()) as { id: string }
  const updated = await request.put(`/api/floors/${floor.id}`, {
    headers: mutationHeaders(),
    data: {
      name,
      level: 0,
      widthCm: 2000,
      heightCm: 1500,
      gridCm: 50,
      background: { fileId: file.id, opacity: 0.35, offsetXCm: 0, offsetYCm: 0, cmPerPx: 2.5 },
      rowVersion: floor.rowVersion,
    },
  })
  expect(updated.status(), await updated.text()).toBe(200)
  floor = (await updated.json()) as { id: number; rowVersion: string }

  const rect = (x0: number, y0: number, x1: number, y1: number) => [
    { x: x0, y: y0 },
    { x: x1, y: y0 },
    { x: x1, y: y1 },
    { x: x0, y: y1 },
  ]
  const layout = await request.put(`/api/floors/${floor.id}/layout`, {
    headers: mutationHeaders(),
    data: {
      rowVersion: floor.rowVersion,
      rooms: [
        {
          id: null,
          name: tr(locale, 'floors.editor.presets.living'),
          icon: 'sofa',
          colorToken: 'room-1',
          polygon: rect(100, 100, 1000, 750),
        },
        {
          id: null,
          name: tr(locale, 'floors.editor.presets.kitchen'),
          icon: 'cooking-pot',
          colorToken: 'room-2',
          polygon: rect(1000, 100, 1900, 900),
        },
        {
          id: null,
          name: tr(locale, 'floors.editor.presets.bedroom'),
          icon: 'bed-double',
          colorToken: 'room-3',
          polygon: rect(100, 750, 1000, 1400),
        },
      ],
    },
  })
  expect(layout.status(), await layout.text()).toBe(200)
  return { id: floor.id, name }
}

/** The floor pages of the seeded floor. */
function floorRoutes(floor: { id: number; name: string }): Route[] {
  const room = (page: Page, locale: Locale) =>
    page
      .locator('[data-slot="floor-plan"] svg text')
      .filter({ hasText: tr(locale, 'floors.editor.presets.kitchen') })
      .first()
  return [
    {
      path: '/floors',
      name: 'floors',
      ready: (page) => page.getByRole('link', { name: floor.name, exact: true }),
    },
    { path: `/floors/${floor.id}`, name: 'floor-view', ready: room },
    { path: `/floors/${floor.id}/edit`, name: 'floor-edit', ready: room },
  ]
}

/** The devices and integrations pages before any integration exists (their empty states). */
const emptyDeviceRoutes: Route[] = [
  {
    path: '/devices',
    name: 'devices-empty',
    ready: (page, locale) => page.getByRole('heading', { name: tr(locale, 'devices.empty.title') }),
  },
  {
    path: '/integrations',
    name: 'integrations-empty',
    ready: (page, locale) => page.getByRole('heading', { name: tr(locale, 'integrations.empty.title') }),
  },
]

type SimulatedHome = Awaited<ReturnType<typeof createSimulatedHome>>

/**
 * The Simulated connector's basic home, its devices spread over the seeded floor's rooms and placed on its plan (the
 * lock and the door sensor stay without a room and off the plan, so the "No room" group and the editor's "Not on a
 * plan" list show too) and the light marked as a favorite.
 */
async function seedDevices(request: APIRequestContext, floorId: number): Promise<SimulatedHome> {
  const home = await createSimulatedHome(request)
  // Living room (100–1000 × 100–750), kitchen (1000–1900 × 100–900), bedroom (100–1000 × 750–1400).
  await placeEndpoint(request, home.light, floorId, 300, 260)
  await placeEndpoint(request, home.plug, floorId, 1720, 280)
  await placeEndpoint(request, home.ac, floorId, 300, 1230)
  const favorite = await request.put(`/api/endpoints/${home.light.endpoints[0]!.id}/favorite`, {
    headers: mutationHeaders(),
    data: { favorite: true },
  })
  expect(favorite.status(), await favorite.text()).toBe(200)
  return home
}

/**
 * The pages of milestones 1.3 and 1.4: the integrations catalogue, the Simulated setup flow's first step, the
 * integration's own page, the editor's Devices tab, a device's history, and the settings pages.
 */
function homeRoutes(home: SimulatedHome, floor: { id: number }, integrationId: string): Route[] {
  return [
    {
      path: '/integrations/add',
      name: 'integrations-catalog',
      ready: (page) => page.locator('[data-connector="simulated"]'),
    },
    {
      path: '/integrations/add/simulated',
      name: 'integration-setup',
      ready: (page) => page.locator('[data-slot="flow-wizard"][data-step="form"]'),
    },
    {
      path: `/integrations/${integrationId}`,
      name: 'integration-detail',
      ready: (page) => page.locator('[data-slot="integration-devices"]').getByText(home.lock.name),
    },
    {
      path: `/floors/${floor.id}/edit?tab=devices`,
      name: 'floor-edit-devices',
      // Below lg the panel is a sheet: the bar under the plan offers to place a device.
      ready: (page, locale) =>
        (page.viewportSize()?.width ?? 1440) < 1024
          ? page.getByRole('button', { name: tr(locale, 'floors.editor.devices.placeShort') })
          : page.locator('[data-slot="unplaced-device"]').filter({ hasText: home.lock.name }),
    },
    {
      path: `/devices/${home.plug.id}?tab=history`,
      name: 'device-plug-history',
      ready: (page) => page.locator('[data-slot="energy-history"] [data-slot="energy-bars"]'),
    },
    {
      path: '/settings/backup',
      name: 'settings-backup',
      ready: (page) => page.locator('[data-slot="backup-export"]'),
    },
    {
      path: '/settings/history',
      name: 'settings-history',
      ready: (page) => page.locator('[data-slot="retention-form"] input').first(),
    },
  ]
}

/** The devices grid, a device page per kind of control, and the integrations list. */
function deviceRoutes(home: SimulatedHome): Route[] {
  const heading = (name: string) => (page: Page) => page.getByRole('heading', { level: 1, name })
  return [
    {
      path: '/devices',
      name: 'devices',
      ready: (page) => page.locator('[data-slot="device-tile"]').filter({ hasText: home.lock.name }),
    },
    { path: `/devices/${home.light.id}`, name: 'device-light', ready: heading(home.light.name) },
    { path: `/devices/${home.ac.id}`, name: 'device-ac', ready: heading(home.ac.name) },
    { path: `/devices/${home.plug.id}`, name: 'device-plug', ready: heading(home.plug.name) },
    { path: `/devices/${home.lock.id}`, name: 'device-lock', ready: heading(home.lock.name) },
    { path: `/devices/${home.contact.id}`, name: 'device-contact', ready: heading(home.contact.name) },
    {
      path: '/integrations',
      name: 'integrations',
      ready: (page) => page.getByRole('heading', { name: 'Simulated home' }),
    },
  ]
}

/** `POST /api/_test/devices/{attributeId}/state`: a value goes through the state pipeline as if the device reported it. */
async function injectState(
  request: APIRequestContext,
  device: SimulatedDevice,
  capabilityId: string,
  key: string,
  value: { kind: 'bool' | 'string'; b?: boolean; s?: string },
): Promise<void> {
  const response = await request.post(
    `/api/_test/devices/${attributeIdOf(device, capabilityId, key)}/state`,
    {
      headers: mutationHeaders(),
      data: { value: { b: null, n: null, s: null, j: null, ...value } },
    },
  )
  expect(response.status(), await response.text()).toBe(204)
}

/** AttributeIds the browser-side color bulb uses (far above any real one). */
const COLOR_ATTRIBUTES = { hue: 990_001, saturation: 990_002, colorTemperature: 990_003 } as const

/**
 * States the basic simulated home cannot reach, simulated in the browser like `simulateNoDatabase` (the device answers
 * are rewritten on their way in): the plug unreachable for 12 minutes, and the light a color bulb glowing violet (a
 * `color` capability the catalogue has but no basic-home device; violet, so it is never mistaken for the warm white of
 * a plain light). Undo with `page.unrouteAll()`.
 */
async function simulateDeviceStates(page: Page, home: SimulatedHome): Promise<void> {
  const now = new Date().toISOString()
  const lastSeen = new Date(Date.now() - 12 * 60_000).toISOString()
  const number = (n: number) => ({ kind: 'number', b: null, n, s: null, j: null })
  const colorValues = [
    { attributeId: COLOR_ATTRIBUTES.hue, key: 'hue', value: number(265) },
    { attributeId: COLOR_ATTRIBUTES.saturation, key: 'saturation', value: number(85) },
    { attributeId: COLOR_ATTRIBUTES.colorTemperature, key: 'colorTemperature', value: number(2700) },
  ].map((attribute) => ({ ...attribute, observedAtUtc: now, lastChangedAtUtc: now, restored: false }))
  const color = {
    id: COLOR_ATTRIBUTES.hue,
    capabilityId: 'color',
    capabilityVersion: 1,
    retired: false,
    constraints: null,
    genericDefinition: null,
    attributes: colorValues,
  }
  type Device = SimulatedDevice & { availability: string; lastSeenAtUtc: string | null }
  const rewrite = (device: Device): Device => {
    if (device.id === home.plug.id) return { ...device, availability: 'Offline', lastSeenAtUtc: lastSeen }
    if (device.id !== home.light.id) return device
    return {
      ...device,
      endpoints: device.endpoints.map((endpoint, index) =>
        index === 0 ? { ...endpoint, capabilities: [...endpoint.capabilities, color] } : endpoint,
      ) as Device['endpoints'],
    }
  }
  await page.route(/\/api\/devices(\?.*)?$/, async (route) => {
    const response = await route.fetch()
    await route.fulfill({ response, json: ((await response.json()) as Device[]).map(rewrite) })
  })
  await page.route(/\/api\/devices\/[0-9a-f-]+$/, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback()
    const response = await route.fetch()
    await route.fulfill({ response, json: rewrite((await response.json()) as Device) })
  })
  await page.route(/\/api\/state$/, async (route) => {
    const response = await route.fetch()
    const extra = colorValues.map(({ attributeId, value, observedAtUtc, lastChangedAtUtc, restored }) => ({
      attributeId,
      value,
      observedAtUtc,
      lastChangedAtUtc,
      restored,
    }))
    await route.fulfill({ response, json: [...((await response.json()) as unknown[]), ...extra] })
  })
}

// Interaction states (English, both widths and themes) into ./screenshots/state-<name>-<width>-<theme>.png: field errors,
// keyboard focus, hover, a busy button, open dialogs, menus and sheets, and a toast. The gates check each one too. The
// phone width runs as a touch screen, so the editor's touch flow (bigger handles, taps, the drawing bar) is captured.
for (const width of WIDTHS) {
  for (const theme of THEMES) {
    test.describe(`states, ${width} px, ${theme}`, () => {
      test.use({
        viewport: { width, height: width < 768 ? 812 : 900 },
        colorScheme: theme,
        hasTouch: width < 768,
      })

      test('interaction states', async ({ page, request, gates }) => {
        const phone = width < 768
        await page.addInitScript((savedTheme) => {
          localStorage.setItem('ch.theme', savedTheme)
          localStorage.setItem('ch.locale', 'en')
        }, theme)

        async function capture(name: string): Promise<void> {
          await page.evaluate(() => document.fonts.ready)
          await gates.check(`state ${name} (${width} px, ${theme})`)
          // Focus states: the first screen only (a full-page capture of the taller dashboard dropped the sidebar's ring).
          // The editor fills the screen: a full-page capture would resize the window and refit the plan mid-gesture.
          const fullPage = !phone && !name.startsWith('focus-') && !name.startsWith('editor-')
          await page.screenshot({ path: `${outputDir}state-${name}-${width}-${theme}.png`, fullPage })
        }

        // Setup: a wrong code is only refused when the wizard finishes, and lands back on the first step.
        await resetApp(request)
        await page.goto('/setup')
        const next = () => page.getByRole('button', { name: t('common.continue') }).click()
        await page.getByLabel(t('setup.code.label')).fill('WRNG-CODE-0000')
        await next()
        await page.getByLabel(t('setup.admin.displayName')).fill(testData.admin.displayName)
        await page.getByLabel(t('setup.admin.userName')).fill(testData.admin.userName)
        await page.getByLabel(t('setup.admin.password'), { exact: true }).fill(testData.admin.password)
        await page.getByLabel(t('setup.admin.confirmPassword')).fill(testData.admin.password)
        await next()
        await page.getByLabel(t('setup.home.name')).fill(testData.home.name)
        await next()
        await page.getByRole('button', { name: t('setup.review.finish') }).click()
        await expect(page.getByText(t('errors.setup.invalid_code'))).toBeVisible()
        await capture('setup-invalid-code')

        // Database step (simulated pre-database mode): a refused login, the script when the login can't create the
        // database, and the progress while the server prepares it.
        await simulateNoDatabase(page)
        await openDatabaseStep(page, 'en')
        await page.getByLabel(t('setup.database.userName'), { exact: true }).fill('controlhome')
        await page.getByLabel(t('setup.database.password'), { exact: true }).fill('not the password')
        const connect = page.getByRole('button', { name: t('setup.database.connect') })
        await page.route('**/api/setup/database', (route) =>
          route.fulfill({
            status: 400,
            contentType: 'application/problem+json',
            json: { status: 400, code: 'db.connection_failed', reason: 'login_failed' },
          }),
        )
        await connect.click()
        await expect(page.getByRole('alert')).toContainText(t('setup.database.reasons.login_failed'))
        await capture('setup-database-error')

        await page.unroute('**/api/setup/database')
        await page.route('**/api/setup/database', (route) =>
          route.fulfill({
            status: 409,
            contentType: 'application/problem+json',
            json: {
              status: 409,
              code: 'db.create_denied',
              script: [
                '-- Run as a SQL Server administrator (for example sa, in SQL Server Management Studio or sqlcmd).',
                '-- Then sign in to the setup wizard with the login ControlHome_App and the password you chose here.',
                'USE [master];',
                'GO',
                'CREATE DATABASE [ControlHome];',
                'GO',
                'ALTER DATABASE [ControlHome] SET COMPATIBILITY_LEVEL = 170;',
                'ALTER DATABASE [ControlHome] SET RECOVERY SIMPLE;',
                'GO',
                "CREATE LOGIN [ControlHome_App] WITH PASSWORD = N'<choose-a-strong-password>', CHECK_POLICY = ON;",
                'GO',
                'USE [ControlHome];',
                'GO',
                'CREATE USER [ControlHome_App] FOR LOGIN [ControlHome_App];',
                'ALTER ROLE [db_owner] ADD MEMBER [ControlHome_App];',
                'GO',
                '',
              ].join('\n'),
            },
          }),
        )
        await connect.click()
        await expect(page.getByText(t('setup.database.createDenied.title'))).toBeVisible()
        await capture('setup-database-script')

        await page.unroute('**/api/setup/database')
        await page.unroute('**/api/setup/status')
        const migrating = {
          setupCompleted: false,
          databaseState: 'Migrating',
          databaseConfigured: true,
          databaseSource: 'file',
          databaseError: null,
          version: 'screenshots',
        }
        await page.route('**/api/setup/status', (route) => route.fulfill({ json: migrating }))
        await page.route('**/api/setup/database', (route) =>
          route.fulfill({ status: 202, json: { ...migrating, databaseState: 'Connecting' } }),
        )
        await page.getByLabel(t('setup.database.password'), { exact: true }).fill('the right one')
        await page.getByRole('button', { name: t('setup.database.checkAgain') }).click()
        await expect(page.getByText(t('setup.database.progress.preparing'), { exact: true })).toBeVisible()
        await capture('setup-database-progress')
        await page.unrouteAll({ behavior: 'ignoreErrors' })

        await completeSetup(request, { locale: 'en' })
        await createUser(request, testData.member, 'Member')

        // Sign-in: a wrong password, keyboard focus on a field and on the button, then a busy button.
        await page.goto('/login')
        const userName = page.getByLabel(t('auth.login.userName'))
        const password = page.getByLabel(t('auth.login.password'), { exact: true })
        const submit = page.getByRole('button', { name: t('auth.login.submit') })
        await userName.fill(testData.admin.userName)
        await password.fill('not the right password')
        await submit.click()
        await expect(page.getByRole('alert')).toContainText(t('errors.auth.invalid_credentials'))
        await capture('login-error')

        await userName.focus()
        await capture('focus-input')
        await page.getByRole('checkbox', { name: t('auth.login.rememberMe') }).focus()
        await page.keyboard.press('Tab')
        await expect(submit).toBeFocused()
        await capture('focus-button')

        // Hold the sign-in request to see the busy button, then let it through.
        let release: () => void = () => {}
        const held = new Promise<void>((resolve) => (release = resolve))
        await page.route('**/api/auth/login', async (route) => {
          await held
          await route.continue()
        })
        await password.fill(testData.admin.password)
        await submit.click()
        await expect(page.getByRole('button', { name: t('auth.login.submitting') })).toHaveAttribute(
          'aria-busy',
          'true',
        )
        await capture('button-loading')
        release()
        await expect(page).toHaveURL(/\/$/)
        await page.unroute('**/api/auth/login')

        // Dashboard: hover on a first step's action, keyboard focus in the navigation (the menu sheet on phones).
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
        await page
          .locator('[data-slot="dashboard-start"] [data-step="floors"]')
          .getByRole('link', { name: t('dashboard.start.floorsAction') })
          .hover()
        await capture('hover-card')
        if (phone) {
          await page.getByRole('button', { name: t('shell.openNavigation') }).click()
          const navigation = page.getByRole('navigation', { name: t('shell.primaryNavigation') })
          await expect(navigation).toBeVisible()
          await navigation.getByRole('link', { name: t('dashboard.nav'), exact: true }).focus()
          await page.keyboard.press('Tab')
          await capture('sheet-open')
          await page.keyboard.press('Escape')
          await expect(navigation).toBeHidden()
        } else {
          const navigation = page.getByRole('navigation', { name: t('shell.primaryNavigation') })
          await navigation.getByRole('link', { name: t('dashboard.nav'), exact: true }).focus()
          await page.keyboard.press('Tab')
          await expect(navigation.getByRole('link', { name: t('devices.nav'), exact: true })).toBeFocused()
          await capture('focus-sidebar')
        }

        // Users: hover on a row, the row menu of your own account, the add dialog, a confirmation and a toast.
        await page.goto('/users')
        const member = page.getByText(testData.member.userName, { exact: true }).filter({ visible: true })
        await expect(member).toBeVisible()
        if (!phone) {
          await page.getByRole('row', { name: new RegExp(testData.member.displayName) }).hover()
          await capture('hover-row')
        }
        const adminMenu = page.getByRole('button', {
          name: t('users.actions.menu', { name: testData.admin.displayName }),
        })
        await adminMenu.click()
        await expect(page.getByRole('menu')).toBeVisible()
        await capture('menu-open')
        await page.keyboard.press('Escape')
        await expect(page.getByRole('menu')).toBeHidden()

        await page.getByRole('button', { name: t('users.add') }).click()
        const dialog = page.getByRole('dialog', { name: t('users.form.createTitle') })
        await expect(dialog).toBeVisible()
        await capture('dialog-open')
        await page.keyboard.press('Escape')
        await expect(dialog).toBeHidden()

        await page
          .getByRole('button', { name: t('users.actions.menu', { name: testData.member.displayName }) })
          .click()
        await page.getByRole('menuitem', { name: t('users.actions.deactivate') }).click()
        const confirm = page.getByRole('alertdialog')
        await expect(confirm).toBeVisible()
        await capture('confirm-open')
        await confirm.getByRole('button', { name: t('users.deactivate.confirm') }).click()
        await expect(confirm).toBeHidden()
        await expect(
          page.getByText(t('users.toast.disabled', { name: testData.member.displayName })),
        ).toBeVisible()
        await capture('toast')

        // Floor-plan editor: a selected room (handles, wall lengths), then a room being drawn.
        const floor = await seedFloor(page.request, 'en')
        await page.goto(`/floors/${floor.id}/edit`)
        const kitchen = page
          .locator('[data-slot="floor-plan"] svg text')
          .filter({ hasText: t('floors.editor.presets.kitchen') })
          .first()
        await expect(kitchen).toBeVisible()
        await dismissToasts(page)
        await tapPlan(page, 1450, 500, phone)
        await capture('editor-room-selected')
        if (!phone) {
          // Hover is a small step up in fill; the selection is the outline ring.
          const living = await planPoint(page, 500, 400)
          await page.mouse.move(living.x, living.y)
          await capture('editor-room-hover')
        }

        // Drawing: three corners placed, the first one ready to close the outline.
        if (phone) {
          await page
            .getByRole('toolbar', { name: t('floors.editor.tools.label') })
            .getByRole('button', { name: t('floors.editor.tools.draw') })
            .click()
        } else {
          await page.keyboard.press('Escape')
          await page.keyboard.press('r')
        }
        for (const [x, y] of [
          [1050, 950],
          [1850, 950],
          [1850, 1350],
        ] as const) {
          await tapPlan(page, x, y, phone)
        }
        if (!phone) {
          const cursor = await planPoint(page, 1300, 1250)
          await page.mouse.move(cursor.x, cursor.y)
          await expect(page.getByText(/^x \d/)).toBeVisible()
        }
        await expect(
          page.getByRole('button', { name: t('floors.editor.drawing.finish', { count: 3 }) }),
        ).toHaveCount(phone ? 1 : 0)
        await capture('editor-drawing')
        if (phone) {
          await page.getByRole('button', { name: t('floors.editor.drawing.finish', { count: 3 }) }).click()
        } else {
          await page.keyboard.press('Enter')
        }
        await expect(
          page.getByRole('button', { name: new RegExp(`^${t('floors.editor.save')}`) }),
        ).toBeEnabled()

        // Deleting a room (the new one is selected) offers to undo it.
        if (phone) {
          await page.getByRole('button', { name: new RegExp(t('floors.editor.openPanel')) }).click()
          const sheet = page.getByRole('dialog', { name: t('floors.editor.properties') })
          await sheet.getByRole('button', { name: t('floors.editor.room.delete') }).click()
          await page.keyboard.press('Escape')
          await expect(sheet).toBeHidden()
        } else {
          await page.keyboard.press('Delete')
        }
        await expect(
          page.getByText(t('floors.editor.room.deleted', { name: `${t('floors.editor.roomBase')} 4` })),
        ).toBeVisible()
        await capture('editor-deleted-toast')
      })
    })
  }
}

// Floor states in every width, theme and language into ./screenshots/floorstate-<name>-<width>-<theme>-<locale>.png: the
// empty floors list, the add dialog, an empty floor in the editor and in view mode, the editor's Floor and Image tabs,
// the calibration dialog, the 409 conflict dialog, the delete confirmation and the view with the original plan shown.
for (const width of WIDTHS) {
  for (const theme of THEMES) {
    for (const locale of LOCALES) {
      test.describe(`floor states, ${width} px, ${theme}, ${locale}`, () => {
        test.use({
          viewport: { width, height: width < 768 ? 812 : 900 },
          colorScheme: theme,
          locale: locale === 'en' ? 'en-US' : 'pt-BR',
          hasTouch: width < 768,
        })

        test('floor states', async ({ page, request, gates }) => {
          const phone = width < 768
          const l = (key: string, params?: Record<string, string | number>) => tr(locale, key, params)
          await page.addInitScript(
            ([savedTheme, savedLocale]) => {
              localStorage.setItem('ch.theme', savedTheme)
              localStorage.setItem('ch.locale', savedLocale)
            },
            [theme, locale] as const,
          )

          async function capture(name: string, fullPage = !phone): Promise<void> {
            await expect(page.locator('html')).toHaveAttribute('lang', locale)
            await page.evaluate(() => document.fonts.ready)
            await gates.check(`floor state ${name} (${width} px, ${theme}, ${locale})`)
            await page.screenshot({
              path: `${outputDir}floorstate-${name}-${width}-${theme}-${locale}.png`,
              fullPage,
            })
          }

          /** The editor's side panel: the aside, or on phones the sheet opened from the bar below the plan. */
          async function panel() {
            if (!phone) return page.getByRole('complementary', { name: l('floors.editor.properties') })
            const sheet = page.getByRole('dialog', { name: l('floors.editor.properties') })
            if (!(await sheet.isVisible())) {
              await page.getByRole('button', { name: new RegExp(l('floors.editor.openPanel')) }).click()
            }
            await expect(sheet).toBeVisible()
            return sheet
          }

          await resetApp(request)
          await completeSetup(page.request, { locale })
          await savePreferences(page.request, { displayName: testData.admin.displayName, locale, theme })

          // No floors yet, and the add dialog.
          await page.goto('/floors')
          await expect(page.getByRole('heading', { name: l('floors.empty.title') })).toBeVisible()
          await capture('floors-empty')
          await page.getByRole('button', { name: l('floors.empty.cta') }).click()
          const add = page.getByRole('dialog', { name: l('floors.form.createTitle') })
          await expect(add).toBeVisible()
          await capture('floors-add-dialog', false)
          await add.getByLabel(l('floors.form.name')).fill(l('floors.level.above', { n: 1 }))
          await add.getByLabel(l('floors.form.level')).fill('1')
          await add.getByRole('button', { name: l('floors.form.create') }).click()

          // An empty floor in the editor, its Floor tab, and the same floor in view mode.
          await expect(page.getByRole('heading', { name: l('floors.editor.empty.title') })).toBeVisible()
          await dismissToasts(page, locale)
          await capture('floor-edit-empty', false)
          let side = await panel()
          await side.getByRole('tab', { name: l('floors.editor.tabs.floor') }).click()
          await expect(side.getByLabel(l('floors.editor.floor.grid'))).toBeVisible()
          await capture('floor-edit-tab-floor', false)
          const emptyId = Number(/\/floors\/(\d+)\/edit/.exec(page.url())![1])
          if (phone) await page.keyboard.press('Escape')
          await page.goto(`/floors/${emptyId}`)
          await expect(page.getByText(l('floors.view.noRoomsTitle'))).toBeVisible()
          await capture('floor-view-empty')

          // A traced floor with rooms: the Image tab, then calibration (two points on the plan).
          const traced = await seedFloor(page.request, locale)
          await page.goto(`/floors/${traced.id}/edit`)
          await expect(page.locator('[data-slot="floor-background"] image')).toHaveCount(1)
          side = await panel()
          await side.getByRole('tab', { name: l('floors.editor.tabs.background') }).click()
          await expect(side.getByTestId('background-scale')).toBeVisible()
          await capture('floor-edit-tab-image', false)
          await side.getByRole('button', { name: l('floors.editor.background.calibrate') }).click()
          if (phone) {
            await page.keyboard.press('Escape')
            await expect(side).toBeHidden()
          }
          await tapPlan(page, 100, 1200, phone)
          await tapPlan(page, 1900, 1200, phone)
          const calibration = page.getByRole('dialog', { name: l('floors.editor.calibration.title') })
          await expect(calibration).toBeVisible()
          // The tap that opened the dialog must not also press its button ("Scale set" would follow).
          const scaleSet = l('floors.editor.calibration.done', { scale: '' }).split(':')[0]!
          await expect(page.getByText(scaleSet)).toHaveCount(0)
          await capture('floor-edit-calibration', false)
          await page.keyboard.press('Escape')
          await expect(calibration).toBeHidden()

          // Someone else saves the floor while it has unsaved changes here: the conflict dialog.
          await page.keyboard.press('Escape')
          await tapPlan(page, 1450, 500, phone)
          await page.keyboard.press('ArrowLeft')
          const current = (await (await page.request.get(`/api/floors/${traced.id}`)).json()) as {
            rowVersion: string
            rooms: { id: number; name: string; icon: string; colorToken: string; polygon: unknown[] }[]
          }
          const stale = await page.request.put(`/api/floors/${traced.id}/layout`, {
            headers: mutationHeaders(),
            data: {
              rowVersion: current.rowVersion,
              rooms: current.rooms.map(({ id, name, icon, colorToken, polygon }) => ({
                id,
                name,
                icon,
                colorToken,
                polygon,
              })),
            },
          })
          expect(stale.status(), await stale.text()).toBe(200)
          await page.getByRole('button', { name: new RegExp(`^${l('floors.editor.save')}`) }).click()
          const conflict = page.getByRole('alertdialog')
          await expect(conflict).toContainText(l('floors.editor.conflict.title'))
          await capture('floor-edit-conflict', false)
          await conflict.getByRole('button', { name: l('floors.editor.conflict.reload') }).click()
          await expect(conflict).toBeHidden()

          // The view with the original plan switched on.
          await page.goto(`/floors/${traced.id}`)
          await page.getByRole('switch', { name: l('floors.view.showOriginal') }).click()
          await expect(page.locator('[data-slot="floor-background"] image')).toHaveCount(1)
          await capture('floor-view-original')

          // Two floors in the list, and the delete confirmation.
          await page.goto('/floors')
          const upper = l('floors.level.above', { n: 1 })
          await page.getByRole('button', { name: l('floors.actions', { name: upper }) }).click()
          await page.getByRole('menuitem', { name: l('common.delete') }).click()
          const confirm = page.getByRole('alertdialog')
          await expect(confirm).toContainText(l('floors.delete.title', { name: upper }))
          await capture('floors-delete-confirm', false)
          await confirm.getByRole('button', { name: l('floors.delete.confirm') }).click()
          await expect(page.getByText(l('floors.toast.deleted', { name: upper }))).toBeVisible()
          await capture('floors-deleted-toast')
        })
      })
    }
  }
}

for (const width of WIDTHS) {
  for (const theme of THEMES) {
    for (const locale of LOCALES) {
      test.describe(`${width} px, ${theme}, ${locale}`, () => {
        test.use({
          viewport: { width, height: width < 768 ? 812 : 900 },
          colorScheme: theme,
          locale: locale === 'en' ? 'en-US' : 'pt-BR',
        })

        test('every route', async ({ page, request, gates }) => {
          // Before sign-in the page follows this browser's saved choice (as after picking it in the top bar).
          await page.addInitScript(
            ([savedTheme, savedLocale]) => {
              localStorage.setItem('ch.theme', savedTheme)
              localStorage.setItem('ch.locale', savedLocale)
            },
            [theme, locale] as const,
          )

          async function capture(route: Route): Promise<void> {
            await page.goto(route.path)
            await expect(route.ready(page, locale)).toBeVisible()
            await expect(page.locator('html')).toHaveAttribute('lang', locale)
            await page.evaluate(() => document.fonts.ready)
            await gates.check(`${route.path} (${width} px, ${theme}, ${locale})`)
            await screenshotWholePage(page, `${outputDir}${route.name}-${width}-${theme}-${locale}.png`)
          }

          /** The screen as it is now (an open dialog), into ./screenshots/<name>-<width>-<theme>-<locale>.png. */
          async function captureScreen(name: string): Promise<void> {
            await expect(page.locator('html')).toHaveAttribute('lang', locale)
            await page.evaluate(() => document.fonts.ready)
            await gates.check(`${name} (${width} px, ${theme}, ${locale})`)
            await page.screenshot({ path: `${outputDir}${name}-${width}-${theme}-${locale}.png` })
          }

          await resetApp(request)
          await capture(anonymousRoutes[0]!)

          // The database step of a server without a database connection (simulated in the browser).
          await simulateNoDatabase(page)
          await openDatabaseStep(page, locale)
          await expect(page.locator('html')).toHaveAttribute('lang', locale)
          await page.evaluate(() => document.fonts.ready)
          await gates.check(`/setup database step (${width} px, ${theme}, ${locale})`)
          await screenshotWholePage(page, `${outputDir}setup-database-${width}-${theme}-${locale}.png`)
          await page.unrouteAll({ behavior: 'ignoreErrors' })

          // Set up through Playwright's own request context, so the browser is still signed out for /login.
          await completeSetup(request, { locale })
          await capture(anonymousRoutes[1]!)

          // Sign the browser in with the same theme and language saved to the profile, and give /users some rows.
          await signIn(page.request, testData.admin)
          await savePreferences(page.request, { displayName: testData.admin.displayName, locale, theme })
          await createUser(page.request, testData.member, 'Member')
          await createUser(page.request, testData.viewer, 'Viewer')
          // A floor with rooms over a traced plan: the dashboard shows it, and the floor pages get captured too.
          const floor = await seedFloor(page.request, locale)
          // No integration yet: the empty devices and integrations pages. Then the simulated home in those rooms.
          for (const route of emptyDeviceRoutes) await capture(route)
          const home = await seedDevices(page.request, floor.id)
          const integrations = (await (await page.request.get('/api/integrations')).json()) as {
            id: string
          }[]
          for (const route of [
            ...signedInRoutes,
            ...floorRoutes(floor),
            ...deviceRoutes(home),
            ...homeRoutes(home, floor, integrations[0]!.id),
          ])
            await capture(route)

          // A device on the plan, tapped: its controls beside it (a sheet on phones).
          await page.goto(`/floors/${floor.id}`)
          await waitForLive(page)
          await expect(page.locator('[data-slot="placed-device"]')).toHaveCount(3)
          await tapPlan(page, 300, 260, false)
          await expect(page.locator('[data-slot="device-quick-panel"]')).toBeVisible()
          await captureScreen('floor-view-device')
          await page.keyboard.press('Escape')
          await expect(page.locator('[data-slot="device-quick-panel"]')).toBeHidden()

          // Phones and tablets: the editor's Devices tab is a sheet over the plan.
          if (width < 1024) {
            await page.goto(`/floors/${floor.id}/edit?tab=devices`)
            await page.getByRole('button', { name: tr(locale, 'floors.editor.devices.placeShort') }).click()
            await expect(
              page.locator('[data-slot="unplaced-device"]').filter({ hasText: home.lock.name }),
            ).toBeVisible()
            await captureScreen('floor-edit-devices-sheet')
            await page.keyboard.press('Escape')
          }

          // The device info opened: every attribute as a table, or as a stacked list on phones.
          await page.goto(`/devices/${home.contact.id}`)
          await expect(page.getByRole('heading', { level: 1, name: home.contact.name })).toBeVisible()
          await page.locator('[data-slot="device-info"] summary').click()
          await expect(
            page.locator(width < 640 ? '[data-slot="attributes-list"]' : '[data-slot="attributes-table"]'),
          ).toBeVisible()
          await page.evaluate(() => document.fonts.ready)
          await gates.check(`device info (${width} px, ${theme}, ${locale})`)
          await screenshotWholePage(page, `${outputDir}device-contact-info-${width}-${theme}-${locale}.png`)

          // Device states, each one unmistakable in this theme and width: the AC off, the lock unlocked, the door open,
          // the plug offline and the light a color bulb (both simulated in the browser), then the lock jammed, and a
          // command on its way (its request held back, so the tile says what it is doing).
          await sendCommand(page.request, home.ac, 'switch', 'turnOff')
          await expect.poll(() => attributeValue(page.request, home.ac.name, 'switch', 'on')).toBe(false)
          await injectState(page.request, home.lock, 'lock', 'lockState', { kind: 'string', s: 'unlocked' })
          await injectState(page.request, home.contact, 'contact', 'open', { kind: 'bool', b: true })
          await simulateDeviceStates(page, home)
          const heading = (name: string) => (page: Page) => page.getByRole('heading', { level: 1, name })
          const lockState = (state: string) => (page: Page, locale: Locale) =>
            page
              .locator('[data-slot="lock-state"]')
              .filter({ hasText: tr(locale, `device.values.lockState.${state}`) })
          for (const route of [
            {
              path: '/devices',
              name: 'devices-states',
              ready: (page: Page) => page.locator('[data-slot="tile-offline"]'),
            },
            {
              path: `/devices/${home.light.id}`,
              name: 'device-light-color',
              ready: (page: Page) => page.locator('[data-capability="color"]'),
            },
            { path: `/devices/${home.ac.id}`, name: 'device-ac-off', ready: heading(home.ac.name) },
            { path: `/devices/${home.plug.id}`, name: 'device-plug-offline', ready: heading(home.plug.name) },
            { path: `/devices/${home.lock.id}`, name: 'device-lock-unlocked', ready: lockState('unlocked') },
          ])
            await capture(route)
          await injectState(page.request, home.lock, 'lock', 'lockState', { kind: 'string', s: 'jammed' })
          await capture({
            path: `/devices/${home.lock.id}`,
            name: 'device-lock-jammed',
            ready: lockState('jammed'),
          })

          let release!: () => void
          const held = new Promise<void>((resolve) => (release = resolve))
          await page.route(/\/api\/endpoints\/[^/]+\/commands$/, async (route) => {
            await held
            await route.continue()
          })
          await page.goto('/devices')
          await waitForLive(page)
          const acTile = page.locator('[data-slot="device-tile"]').filter({ hasText: home.ac.name }).first()
          await acTile.scrollIntoViewIfNeeded()
          await acTile.getByRole('switch').click()
          await expect(acTile.locator('[data-slot="tile-state"]')).toHaveAttribute('data-pending', 'true')
          await captureScreen('devices-pending')
          release()
          await expect(acTile.locator('[data-slot="tile-state"]')).not.toHaveAttribute('data-pending', {
            timeout: 15_000,
          })
          await page.unrouteAll({ behavior: 'ignoreErrors' })
          await injectState(page.request, home.lock, 'lock', 'lockState', { kind: 'string', s: 'locked' })

          // The safety PIN: the profile section once a PIN is set, then the PIN pad on the lock (two digits typed, then
          // after a wrong PIN). The pad is a dialog: the screen as it is, not the whole page.
          const pin = await page.request.put('/api/auth/me/pin', {
            headers: mutationHeaders(),
            data: { currentPin: null, newPin: '4826', currentPassword: testData.admin.password },
          })
          expect(pin.status(), await pin.text()).toBe(204)
          await capture({
            path: '/profile#safety-pin',
            name: 'profile-pin',
            ready: (page) => page.locator('#safety-pin').getByText(tr(locale, 'profile.pin.isSet')),
          })
          await page.goto(`/devices/${home.lock.id}`)
          await expect(page.getByRole('heading', { level: 1, name: home.lock.name })).toBeVisible()
          await expect(page.locator('[data-realtime-status="connected"]')).toBeAttached({ timeout: 15_000 })
          await page
            .getByRole('button', { name: tr(locale, 'device.lock.unlockAction'), exact: true })
            .click()
          const unlock = tr(locale, 'device.commands.unlock')
          const pad = page.getByRole('dialog', {
            name: tr(locale, 'safety.dialog.question', { action: unlock, name: home.lock.name }),
          })
          await expect(pad).toBeVisible()
          for (const digit of '12') await pad.getByRole('button', { name: digit, exact: true }).click()
          await captureScreen('pin-dialog')
          for (const digit of '34') await pad.getByRole('button', { name: digit, exact: true }).click()
          await pad.getByRole('button', { name: unlock, exact: true }).click()
          await expect(pad.locator('[data-slot="pin-error"]')).toHaveText(
            tr(locale, 'errors.safety.pin_incorrect'),
          )
          await captureScreen('pin-dialog-error')
          await page.keyboard.press('Escape')
          await expect(pad).toBeHidden()
        })
      })
    }
  }
}
