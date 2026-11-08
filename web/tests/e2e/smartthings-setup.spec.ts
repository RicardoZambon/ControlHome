import type { Page } from '@playwright/test'
import { completeSetup, expect, resetApp, test } from './fixtures'

// The SmartThings setup wizard up to the step that needs a real Samsung account: the three ways to sign in, the
// "ControlHome creates your app" step (the tokens page and the token field) and the CLI instructions, each a numbered
// list with one step per line. Nothing here talks to SmartThings: no token is ever submitted.

const wizardAt = (page: Page, type: string) => page.locator(`[data-slot="flow-wizard"][data-step="${type}"]`)

/** The wizard's description, as the reader sees it (line breaks included). */
const description = (page: Page) => page.locator('[data-slot="flow-wizard"] header p.whitespace-pre-line')

test.beforeEach(async ({ page, request }) => {
  await resetApp(request)
  await completeSetup(page.request)
})

test('the SmartThings wizard offers to create the app and lists every step on its own line', async ({
  page,
  gates,
}, testInfo) => {
  await page.goto('/integrations/add/smartthings')

  // The first step: ControlHome creates the app (recommended), an existing CLI app, or a short-lived token.
  await expect(wizardAt(page, 'choose')).toBeVisible()
  await expect(page.getByRole('radio')).toHaveCount(3)
  await expect(page.getByText('Let ControlHome create your app (recommended)')).toBeVisible()
  await expect(page.getByText('I already have a SmartThings app')).toBeVisible()
  await gates.check('smartthings, sign-in choice')
  await page.screenshot({ path: testInfo.outputPath('1-choice.png'), fullPage: true })

  // ControlHome creates the app: the tokens page, the steps one per line, the token field (a secret).
  await page.getByText('Let ControlHome create your app (recommended)').click()
  await page.locator('[data-slot="flow-continue"]').click()
  await expect(wizardAt(page, 'externalUrl')).toBeVisible()
  await expect(page.getByRole('link', { name: /account\.smartthings\.com/ })).toHaveAttribute(
    'href',
    'https://account.smartthings.com/tokens',
  )
  const createSteps = await description(page).innerText()
  expect(createSteps.trim().split('\n')).toHaveLength(4)
  expect(createSteps.trim()).toMatch(/^1\. Open the SmartThings tokens page/)
  await expect(page.getByLabel('SmartThings token')).toHaveAttribute('type', 'password')
  await gates.check('smartthings, create the app')
  await page.screenshot({ path: testInfo.outputPath('2-create-app.png'), fullPage: true })

  // Back to the start, then the CLI path: five steps, the renamed smartapps:create command.
  await page.goto('/integrations/add/smartthings')
  await page.getByText('I already have a SmartThings app').click()
  await page.locator('[data-slot="flow-continue"]').click()
  await expect(wizardAt(page, 'externalUrl')).toBeVisible()
  const cliSteps = await description(page).innerText()
  expect(cliSteps.trim().split('\n')).toHaveLength(5)
  expect(cliSteps).toContain('npx @smartthings/cli smartapps:create')
  await gates.check('smartthings, CLI instructions')
  await page.screenshot({ path: testInfo.outputPath('3-cli-instructions.png'), fullPage: true })
})
