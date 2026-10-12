import { defineConfig, type PlaywrightTestConfig } from '@playwright/test'
import { baseURL } from './env'

// End-to-end specs against a running ControlHome in the Testing environment (so POST /api/_test/reset exists):
//   pwsh scripts/e2e.ps1                                  builds the image, runs it on the dev SQL server, runs this
//   E2E_BASE_URL=http://localhost:8080 npm run e2e        against an app started some other way (CI: compose)
// Every test resets the app's data first, so the tests share one server and run one at a time.

/** Settings shared with playwright.screenshots.config.ts. */
export const shared = {
  testDir: '.',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL,
    locale: 'en-US',
    timezoneId: 'America/Sao_Paulo',
    colorScheme: 'light',
    // Entrance animations off: stable screenshots, and axe never measures contrast mid-fade.
    contextOptions: { reducedMotion: 'reduce' },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
} satisfies PlaywrightTestConfig

export default defineConfig({
  ...shared,
  testIgnore: ['screenshots.spec.ts'],
  outputDir: 'test-results/e2e',
  reporter: [['list'], ['html', { outputFolder: 'playwright-report/e2e', open: 'never' }]],
  projects: [
    { name: 'desktop', use: { viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { viewport: { width: 375, height: 812 }, hasTouch: true } },
  ],
})
