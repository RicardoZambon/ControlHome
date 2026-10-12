import { fileURLToPath } from 'node:url'
import type { Locator, Page } from '@playwright/test'
import {
  completeSetup,
  createUser,
  expect,
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

// Interaction states (English, both widths and themes) into ./screenshots/state-<name>-<width>-<theme>.png: field errors,
// keyboard focus, hover, a busy button, open dialogs, menus and sheets, and a toast. The gates check each one too.
for (const width of WIDTHS) {
  for (const theme of THEMES) {
    test.describe(`states, ${width} px, ${theme}`, () => {
      test.use({ viewport: { width, height: width < 768 ? 812 : 900 }, colorScheme: theme })

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
          const fullPage = !phone && !name.startsWith('focus-')
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
          await expect(navigation.getByRole('link', { name: t('users.nav'), exact: true })).toBeFocused()
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
      })
    })
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

          // Set up through Playwright's own request context, so the browser is still signed out for /login.
          await completeSetup(request, { locale })
          await capture(anonymousRoutes[1]!)

          // Sign the browser in with the same theme and language saved to the profile, and give /users some rows.
          await signIn(page.request, testData.admin)
          await savePreferences(page.request, { displayName: testData.admin.displayName, locale, theme })
          await createUser(page.request, testData.member, 'Member')
          await createUser(
            page.request,
            { userName: 'linus', displayName: 'Linus Torvalds', password: 'just for fun, really' },
            'Viewer',
          )
          for (const route of signedInRoutes) await capture(route)
        })
      })
    }
  }
}
