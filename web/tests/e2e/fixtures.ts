import { readFileSync } from 'node:fs'
import AxeBuilder from '@axe-core/playwright'
import {
  test as base,
  expect,
  type APIRequestContext,
  type ConsoleMessage,
  type Page,
} from '@playwright/test'
import { SETUP_CODE, baseURL } from './env'
import { tr, type Locale } from './i18n'

export { SETUP_CODE, expect }

// --- Test data -------------------------------------------------------------------------------------------------------

export interface TestUser {
  userName: string
  displayName: string
  password: string
}

interface TestData {
  admin: TestUser
  member: TestUser
  home: { name: string; timeZoneId: string; locale: string; currency: string }
}

/** Non-secret test accounts and the home used by every spec (`fixtures/users.json`). */
export const testData: TestData = JSON.parse(
  readFileSync(new URL('./fixtures/users.json', import.meta.url), 'utf8'),
) as TestData

// --- API helpers (Playwright's request context, not the SPA) ---------------------------------------------------------

/** Every mutating request needs the app's own Origin and `X-ControlHome: 1` (else 403 request.cross_origin). */
export function mutationHeaders(): Record<string, string> {
  return { Origin: new URL(baseURL).origin, 'X-ControlHome': '1' }
}

/** `POST /api/_test/reset` (Testing environment only): no users, no home, setup not completed, original setup code. */
export async function resetApp(request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/_test/reset', { headers: mutationHeaders() })
  expect(
    response.status(),
    'POST /api/_test/reset failed: is the app running with ASPNETCORE_ENVIRONMENT=Testing?',
  ).toBe(204)
}

/**
 * Completes the first-run setup through the API with `testData.admin` and `testData.home`. The admin is signed in on
 * `request`: pass `page.request` to sign in the browser too.
 */
export async function completeSetup(
  request: APIRequestContext,
  home: Partial<TestData['home']> = {},
): Promise<void> {
  const response = await request.post('/api/setup/complete', {
    headers: mutationHeaders(),
    data: {
      setupCode: SETUP_CODE,
      admin: testData.admin,
      home: { latitude: null, longitude: null, pricePerKwh: null, ...testData.home, ...home },
    },
  })
  expect(response.status(), `POST /api/setup/complete: ${await response.text()}`).toBe(200)
}

/** Signs `user` in on `request` (`page.request` signs in the browser). */
export async function signIn(request: APIRequestContext, user: TestUser): Promise<void> {
  const response = await request.post('/api/auth/login', {
    headers: mutationHeaders(),
    data: { userName: user.userName, password: user.password, rememberMe: true },
  })
  expect(response.status(), `POST /api/auth/login: ${await response.text()}`).toBe(200)
}

/** Creates an account (the request context must be signed in as an Admin). */
export async function createUser(
  request: APIRequestContext,
  user: TestUser,
  role: 'Admin' | 'Member' | 'Viewer',
): Promise<void> {
  const response = await request.post('/api/users', {
    headers: mutationHeaders(),
    data: { ...user, role },
  })
  expect(response.status(), `POST /api/users: ${await response.text()}`).toBe(201)
}

/** Saves the signed-in user's language and theme (`PUT /api/auth/me/profile`). */
export async function savePreferences(
  request: APIRequestContext,
  preferences: { displayName: string; locale: 'en' | 'pt-BR'; theme: 'light' | 'dark' | 'system' },
): Promise<void> {
  const response = await request.put('/api/auth/me/profile', {
    headers: mutationHeaders(),
    data: preferences,
  })
  expect(response.status(), `PUT /api/auth/me/profile: ${await response.text()}`).toBe(200)
}

// --- Devices ---------------------------------------------------------------------------------------------------------

/** The parts of `GET /api/devices` the specs use. */
export interface SimulatedDevice {
  id: string
  name: string
  deviceClass: string | null
  endpoints: {
    id: string
    capabilities: {
      capabilityId: string
      attributes: {
        attributeId: number
        key: string
        value: { kind: string; b?: boolean; n?: number; s?: string }
      }[]
    }[]
  }[]
}

/** The Simulated connector's basic home (Task 1.2-B), by its device names. */
export const SIMULATED = {
  light: 'Living room light',
  plug: 'Coffee maker plug',
  ac: 'Bedroom air conditioner',
  contact: 'Front door sensor',
  lock: 'Front door lock',
} as const

export async function listDevices(request: APIRequestContext): Promise<SimulatedDevice[]> {
  const response = await request.get('/api/devices')
  expect(response.status(), `GET /api/devices: ${await response.text()}`).toBe(200)
  return (await response.json()) as SimulatedDevice[]
}

/**
 * Adds the Simulated connector's basic home (`POST /api/integrations`, the request context must be an Admin's) and waits
 * until its five devices are in. Returns them by name.
 */
export async function createSimulatedHome(
  request: APIRequestContext,
): Promise<Record<keyof typeof SIMULATED, SimulatedDevice>> {
  const response = await request.post('/api/integrations', {
    headers: mutationHeaders(),
    data: { connectorKey: 'simulated', name: 'Simulated home', config: { preset: 'basic' } },
  })
  expect(response.status(), `POST /api/integrations: ${await response.text()}`).toBe(201)
  let devices: SimulatedDevice[] = []
  await expect
    .poll(
      async () => {
        devices = await listDevices(request)
        return devices.length
      },
      { message: 'the simulated devices to appear', timeout: 30_000 },
    )
    .toBe(5)
  const byName = (name: string) => devices.find((device) => device.name === name)!
  return {
    light: byName(SIMULATED.light),
    plug: byName(SIMULATED.plug),
    ac: byName(SIMULATED.ac),
    contact: byName(SIMULATED.contact),
    lock: byName(SIMULATED.lock),
  }
}

/** The current value of `capability.attribute` of a device (`GET /api/devices`). */
export async function attributeValue(
  request: APIRequestContext,
  deviceName: string,
  capabilityId: string,
  key: string,
): Promise<boolean | number | string | undefined> {
  const device = (await listDevices(request)).find((entry) => entry.name === deviceName)
  const attribute = device?.endpoints
    .flatMap((endpoint) => endpoint.capabilities)
    .find((capability) => capability.capabilityId === capabilityId)
    ?.attributes.find((entry) => entry.key === key)
  const value = attribute?.value
  return value?.b ?? value?.n ?? value?.s
}

/** The AttributeId of `capability.attribute` of a device. */
export function attributeIdOf(device: SimulatedDevice, capabilityId: string, key: string): number {
  const attribute = device.endpoints
    .flatMap((endpoint) => endpoint.capabilities)
    .find((capability) => capability.capabilityId === capabilityId)
    ?.attributes.find((entry) => entry.key === key)
  expect(attribute, `${device.name} has ${capabilityId}.${key}`).toBeDefined()
  return attribute!.attributeId
}

/** Waits until the page's live connection to the hub is up (devices update without a reload from then on). */
export async function waitForLive(page: Page): Promise<void> {
  await expect(page.locator('[data-realtime-status="connected"]')).toBeAttached({ timeout: 15_000 })
}

// --- UI helpers ------------------------------------------------------------------------------------------------------

/** Phones get the sidebar as a sheet (below Tailwind's md breakpoint). */
export function isPhone(page: Page): boolean {
  return (page.viewportSize()?.width ?? 1440) < 768
}

/** The main navigation, opening the menu sheet first on phones (they also get a bottom tab bar). */
export async function openNavigation(page: Page, locale: Locale = 'en') {
  if (isPhone(page)) await page.getByRole('button', { name: tr(locale, 'shell.openNavigation') }).click()
  const navigation = page.getByRole('navigation', { name: tr(locale, 'shell.primaryNavigation') })
  await expect(navigation).toBeVisible()
  return navigation
}

/** Closes the menu sheet that `openNavigation` opened on phones (on wider screens the sidebar stays). */
export async function closeNavigation(page: Page, locale: Locale = 'en'): Promise<void> {
  if (!isPhone(page)) return
  await page.keyboard.press('Escape')
  await expect(page.getByRole('navigation', { name: tr(locale, 'shell.primaryNavigation') })).toBeHidden()
}

/** Dismisses the visible toasts, so they cover nothing the test looks at next. */
export async function dismissToasts(page: Page, locale: Locale = 'en'): Promise<void> {
  const notifications = page.getByRole('region', {
    name: new RegExp(`^${tr(locale, 'shell.notifications')}`),
  })
  const close = notifications.getByRole('button', { name: tr(locale, 'common.close') })
  await expect(async () => {
    if ((await close.count()) > 0) await close.first().click({ timeout: 1_000 })
    await expect(close).toHaveCount(0, { timeout: 1_000 })
  }).toPass({ timeout: 10_000 })
}

/** Signs in on the sign-in page (the page must be showing it). */
export async function signInWithForm(page: Page, user: TestUser, locale: Locale = 'en'): Promise<void> {
  await page.getByLabel(tr(locale, 'auth.login.userName')).fill(user.userName)
  await page.getByLabel(tr(locale, 'auth.login.password'), { exact: true }).fill(user.password)
  await page.getByRole('button', { name: tr(locale, 'auth.login.submit') }).click()
}

/** Opens the account menu in the top bar and signs out. */
export async function signOutFromMenu(page: Page, displayName: string, locale: Locale = 'en'): Promise<void> {
  await page.getByRole('button', { name: tr(locale, 'shell.userMenu', { name: displayName }) }).click()
  await page.getByRole('menuitem', { name: tr(locale, 'shell.logout') }).click()
  await expect(page).toHaveURL(/\/login$/)
}

/** Waits for the profile save that the top bar's theme and language menus start for a signed-in user. */
export function profileSaved(page: Page) {
  return page.waitForResponse(
    (response) => response.request().method() === 'PUT' && response.url().endsWith('/api/auth/me/profile'),
  )
}

// --- Global gates ----------------------------------------------------------------------------------------------------

export interface Gates {
  /**
   * Checks the page as it is now: no axe-core violation of impact serious or critical, and no horizontal overflow when
   * the viewport is 375 px wide or less. Console errors and missing i18n keys are collected for the whole test and
   * checked when it ends; the final page is checked automatically too.
   */
  check(label?: string): Promise<void>
}

/** Width at or below which the page must not scroll sideways. */
const PHONE_WIDTH = 375

/** Console text of a missing translation: the app's own warning, or vue-i18n's (development builds). */
const MISSING_KEY = /\[i18n\] Missing key|\[intlify\] Not found '.+' key in/

/**
 * Chromium logs every 4xx answer as a console error ("Failed to load resource"). API answers such as 401 from the
 * session probe or 400 for a wrong setup code are part of the flows under test, so they are not failures; anything
 * else (a 5xx, a missing script or style, an exception) is.
 */
function isExpectedApiAnswer(message: ConsoleMessage): boolean {
  if (!/^Failed to load resource: the server responded with a status of 4\d\d\b/.test(message.text()))
    return false
  try {
    return new URL(message.location().url).pathname.startsWith('/api/')
  } catch {
    return false
  }
}

/**
 * Lets running transitions and entrance animations end (a button fading back from `disabled:opacity-50`, a dialog
 * zooming in), so axe measures the colors users settle on. Endless animations (spinners) are not waited for.
 */
async function waitForAnimations(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const finite = document.getAnimations().filter((animation) => {
      const end = animation.effect?.getComputedTiming().endTime
      return typeof end === 'number' && Number.isFinite(end)
    })
    await Promise.race([
      Promise.allSettled(finite.map((animation) => animation.finished)),
      new Promise((resolve) => setTimeout(resolve, 2_000)),
    ])
  })
}

async function checkAccessibility(page: Page, label: string): Promise<void> {
  await waitForAnimations(page)
  const results = await new AxeBuilder({ page }).analyze()
  const violations = results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({
      rule: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes
        .slice(0, 5)
        .map((node) => `${node.target.join(' ')}: ${node.failureSummary ?? ''}`),
    }))
  expect(violations, `axe-core serious/critical violations on ${label}`).toEqual([])
}

async function checkOverflow(page: Page, label: string): Promise<void> {
  const width = page.viewportSize()?.width
  if (width === undefined || width > PHONE_WIDTH) return
  const overflow = await page.evaluate(() => {
    const root = document.scrollingElement ?? document.documentElement
    const limit = root.clientWidth
    const offenders = [...document.body.querySelectorAll<HTMLElement>('*')]
      .filter((element) => element.getBoundingClientRect().right > limit + 1)
      .slice(0, 5)
      .map((element) => `<${element.tagName.toLowerCase()} class="${element.className}">`)
    return { scrollWidth: root.scrollWidth, clientWidth: limit, offenders }
  })
  expect(
    overflow.scrollWidth,
    `horizontal overflow on ${label} at ${width} px; widest elements: ${overflow.offenders.join(', ')}`,
  ).toBeLessThanOrEqual(overflow.clientWidth)
}

export async function checkPage(page: Page, label = page.url()): Promise<void> {
  await checkAccessibility(page, label)
  await checkOverflow(page, label)
}

export const test = base.extend<{ gates: Gates }>({
  gates: [
    async ({ page }, use, testInfo) => {
      const problems: string[] = []
      page.on('console', (message) => {
        const text = message.text()
        if (message.type() === 'error' && !isExpectedApiAnswer(message))
          problems.push(`console error: ${text} (${message.location().url})`)
        else if (MISSING_KEY.test(text)) problems.push(`missing i18n key: ${text}`)
      })
      page.on('pageerror', (error) => problems.push(`uncaught exception: ${error.message}`))

      await use({ check: (label) => checkPage(page, label) })

      // The last page the test saw, unless the test already failed (its own error is the interesting one) or skipped
      // itself before opening any page.
      if (testInfo.status === testInfo.expectedStatus && testInfo.status !== 'skipped' && !page.isClosed())
        await checkPage(page, `${page.url()} (end of test)`)
      expect(problems, 'console errors, uncaught exceptions or missing i18n keys').toEqual([])
    },
    { auto: true },
  ],
})
