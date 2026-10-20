import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import type { APIRequestContext, Locator, Page } from '@playwright/test'
import {
  SETUP_CODE,
  completeSetup,
  createSimulatedHome,
  createUser,
  dismissToasts,
  expect,
  mutationHeaders,
  resetApp,
  savePreferences,
  signIn,
  test,
  testData,
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
    ready: (page, locale) =>
      page.getByText(tr(locale, 'dashboard.welcome', { home: testData.home.name }), { exact: true }),
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
 * The Simulated connector's basic home, its devices spread over the seeded floor's rooms (the lock and the door sensor
 * stay without a room, so the "No room" group shows too) and the light marked as a favorite.
 */
async function seedDevices(request: APIRequestContext): Promise<SimulatedHome> {
  const home = await createSimulatedHome(request)
  const rooms = (await (await request.get('/api/rooms')).json()) as { id: number }[]
  const place = async (device: SimulatedHome[keyof SimulatedHome], roomId: number | undefined) => {
    const response = await request.put(`/api/devices/${device.id}`, {
      headers: mutationHeaders(),
      data: { name: device.name, icon: null, roomId: roomId ?? null },
    })
    expect(response.status(), await response.text()).toBe(200)
  }
  await place(home.light, rooms[0]?.id)
  await place(home.plug, rooms[1]?.id)
  await place(home.ac, rooms[2]?.id)
  const favorite = await request.put(`/api/endpoints/${home.light.endpoints[0]!.id}/favorite`, {
    headers: mutationHeaders(),
    data: { favorite: true },
  })
  expect(favorite.status(), await favorite.text()).toBe(200)
  return home
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

/** Where floor point (x, y) cm is on the screen: from the plan SVG's viewBox (cm) and its box (px). */
async function planPoint(page: Page, x: number, y: number): Promise<{ x: number; y: number }> {
  const svg = page.locator('[data-slot="floor-plan"] svg').first()
  const box = (await svg.boundingBox())!
  const [vx, vy, vw, vh] = (await svg.getAttribute('viewBox'))!.split(' ').map(Number) as [
    number,
    number,
    number,
    number,
  ]
  const scale = Math.min(box.width / vw, box.height / vh)
  // preserveAspectRatio xMidYMid meet: the view box is centred in the element.
  const left = box.x + (box.width - vw * scale) / 2
  const top = box.y + (box.height - vh * scale) / 2
  return { x: left + (x - vx) * scale, y: top + (y - vy) * scale }
}

/** A tap on a touch screen (phones, `hasTouch`), a click elsewhere. */
async function tapPlan(page: Page, x: number, y: number, touch: boolean): Promise<void> {
  const point = await planPoint(page, x, y)
  if (touch) await page.touchscreen.tap(point.x, point.y)
  else await page.mouse.click(point.x, point.y)
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

        // Dashboard: hover on a card row, keyboard focus in the navigation (the menu sheet on phones).
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
        await page.getByRole('link', { name: new RegExp(`^${t('dashboard.start.profileTitle')}`) }).hover()
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
          await expect(navigation.getByRole('link', { name: t('floors.nav'), exact: true })).toBeFocused()
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

        // Devices: a tile pressed (pending), and the safety PIN pad (empty, then after a wrong PIN).
        const pin = await page.request.put('/api/auth/me/pin', {
          headers: mutationHeaders(),
          data: { currentPin: null, newPin: '4826' },
        })
        expect(pin.status(), await pin.text()).toBe(204)
        const home = await seedDevices(page.request)
        await page.goto(`/devices/${home.lock.id}`)
        await expect(page.locator('[data-realtime-status="connected"]')).toBeAttached({ timeout: 15_000 })
        await page.getByRole('button', { name: t('device.commands.unlock'), exact: true }).click()
        const pad = page.getByRole('dialog', { name: t('safety.dialog.title') })
        await expect(pad).toBeVisible()
        await pad.getByRole('button', { name: '1', exact: true }).click()
        await pad.getByRole('button', { name: '2', exact: true }).click()
        await capture('pin-dialog')
        for (const digit of '34') await pad.getByRole('button', { name: digit, exact: true }).click()
        await pad.getByRole('button', { name: t('safety.dialog.confirm') }).click()
        await expect(pad.locator('[data-slot="pin-error"]')).toHaveText(t('errors.safety.pin_incorrect'))
        await capture('pin-dialog-error')
        await page.keyboard.press('Escape')
        await expect(pad).toBeHidden()
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
          const home = await seedDevices(page.request)
          for (const route of [...signedInRoutes, ...floorRoutes(floor), ...deviceRoutes(home)])
            await capture(route)
        })
      })
    }
  }
}
