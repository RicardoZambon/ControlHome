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
import { LOCALES, tr, type Locale } from './i18n'

// `npm run screenshots` (playwright.screenshots.config.ts): every route × {1440, 375} px × {light, dark} × {en, pt-BR}
// into ./screenshots/<route>-<width>-<theme>-<locale>.png (gitignored), for visual review. The global gates run on every
// page too, so each theme and language is checked for contrast, overflow and missing translations.

const outputDir = fileURLToPath(new URL('./screenshots/', import.meta.url))
const WIDTHS = [1440, 375] as const
const THEMES = ['light', 'dark'] as const

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
            await page.screenshot({
              path: `${outputDir}${route.name}-${width}-${theme}-${locale}.png`,
              fullPage: true,
            })
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
