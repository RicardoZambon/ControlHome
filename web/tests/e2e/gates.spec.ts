import { test } from './fixtures'

// The global gates themselves (fixtures.ts): each page below breaks exactly one rule, so each test is expected to fail.
// A gate that stops catching its problem turns its test into an unexpected pass, which fails the run.

const page375 = { width: 375, height: 812 }
const html = (body: string) =>
  `<!doctype html><html lang="en"><title>Gate check</title><main>${body}</main></html>`

test('a console error fails the test', async ({ page }) => {
  test.fail()
  await page.setContent(html('<h1>Console</h1>'))
  await page.evaluate(() => console.error('Something broke'))
})

test('an uncaught exception fails the test', async ({ page }) => {
  test.fail()
  await page.setContent(html('<h1>Exception</h1>'))
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        setTimeout(() => {
          throw new Error('Uncaught in a timer')
        }, 0)
        setTimeout(resolve, 50)
      }),
  )
})

test('a missing translation fails the test', async ({ page }) => {
  test.fail()
  await page.setContent(html('<h1>Translations</h1>'))
  await page.evaluate(() => console.warn('[i18n] Missing key "shell.nope" for locale "pt-BR".'))
})

test('a serious accessibility violation fails the check', async ({ page, gates }) => {
  test.fail()
  // A button without an accessible name (axe rule button-name, critical).
  await page.setContent(html('<h1>Accessibility</h1><button type="button"></button>'))
  await gates.check('unnamed button')
})

test('horizontal scrolling at 375 px fails the check', async ({ page, gates }) => {
  test.fail()
  await page.setViewportSize(page375)
  await page.setContent(html('<h1>Overflow</h1><p style="width: 600px">Too wide for a phone.</p>'))
  await gates.check('wide paragraph')
})
