import { defineConfig } from '@playwright/test'
import { shared } from './playwright.config'

// `npm run screenshots`: every route at 1440/375 px × light/dark × en/pt-BR into ./screenshots (gitignored), for visual
// review. Its own output and report folders, so it never replaces the results of an `npm run e2e` run.
export default defineConfig({
  ...shared,
  timeout: 10 * 60_000,
  retries: 0,
  testMatch: ['screenshots.spec.ts'],
  outputDir: 'test-results/screenshots',
  reporter: [['list'], ['html', { outputFolder: 'playwright-report/screenshots', open: 'never' }]],
  projects: [{ name: 'screenshots' }],
})
