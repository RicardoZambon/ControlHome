import { fileURLToPath } from 'node:url'
import type { APIRequestContext, Locator, Page } from '@playwright/test'
import {
  completeSetup,
  createUser,
  dismissToasts,
  expect,
  isPhone,
  mutationHeaders,
  openNavigation,
  resetApp,
  signIn,
  test,
  testData,
} from './fixtures'
import { t } from './i18n'

// The floor-plan editor (Task 1.1-B): create a floor, draw a room by clicking its corners on the SVG canvas, rename and
// color it, undo/redo, save, reload; trace and calibrate a background image; a stale save gets the reload dialog; a
// Member only gets the plan in view mode.

const PLAN_PNG = fileURLToPath(new URL('./fixtures/plan.png', import.meta.url))

interface FloorJson {
  id: number
  name: string
  rowVersion: string
  rooms: { id: number; name: string; colorToken: string; polygon: { x: number; y: number }[] }[]
  background: null | {
    fileId: string
    cmPerPx: number
    offsetXCm: number
    offsetYCm: number
    opacity: number
  }
}

async function apiCreateFloor(request: APIRequestContext, name = 'Ground floor'): Promise<FloorJson> {
  const response = await request.post('/api/floors', {
    headers: mutationHeaders(),
    data: { name, level: 0, widthCm: 2000, heightCm: 1500 },
  })
  expect(response.status(), await response.text()).toBe(201)
  return (await response.json()) as FloorJson
}

async function apiGetFloor(request: APIRequestContext, id: number): Promise<FloorJson> {
  const response = await request.get(`/api/floors/${id}`)
  expect(response.status()).toBe(200)
  return (await response.json()) as FloorJson
}

async function apiSaveLayout(request: APIRequestContext, floor: FloorJson): Promise<FloorJson> {
  const response = await request.put(`/api/floors/${floor.id}/layout`, {
    headers: mutationHeaders(),
    data: {
      rowVersion: floor.rowVersion,
      rooms: [
        {
          id: null,
          name: 'Kitchen',
          icon: 'cooking-pot',
          colorToken: 'room-2',
          polygon: [
            { x: 200, y: 200 },
            { x: 800, y: 200 },
            { x: 800, y: 700 },
            { x: 200, y: 700 },
          ],
        },
      ],
    },
  })
  expect(response.status(), await response.text()).toBe(200)
  return (await response.json()) as FloorJson
}

/** The plan's SVG (the editor's or the view's). */
const planSvg = (page: Page) => page.locator('[data-slot="floor-plan"] svg').first()

/** Where floor point (x, y) cm is on the screen: from the SVG's viewBox (cm) and its box (px). */
async function screenPoint(page: Page, x: number, y: number): Promise<{ x: number; y: number }> {
  const svg = planSvg(page)
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

async function clickPlan(page: Page, x: number, y: number): Promise<void> {
  const point = await screenPoint(page, x, y)
  await page.mouse.click(point.x, point.y)
}

/** A room on the plan: a button named "<name>, <area>" (its visible label may be shortened on small screens). */
const roomLabel = (page: Page, name: string): Locator =>
  planSvg(page).getByRole('button', { name: new RegExp(`^${name}, `) })

/** The editor's side panel (a sheet on phones, opened from the tool bar). */
async function openPanel(page: Page): Promise<Locator> {
  if (isPhone(page)) {
    const sheet = page.getByRole('dialog', { name: t('floors.editor.properties') })
    if (!(await sheet.isVisible())) {
      await page
        .getByRole('toolbar')
        .getByRole('button', { name: t('floors.editor.properties') })
        .click()
    }
    await expect(sheet).toBeVisible()
    return sheet
  }
  return page.getByRole('complementary', { name: t('floors.editor.properties') })
}

async function closePanel(page: Page): Promise<void> {
  if (!isPhone(page)) return
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: t('floors.editor.properties') })).toBeHidden()
}

test.beforeEach(async ({ page }) => {
  await resetApp(page.request)
  // Through the page's request context: the browser is signed in as the admin.
  await completeSetup(page.request)
})

test('an admin creates a floor, draws a room by clicking its corners and saves it', async ({
  page,
  gates,
}) => {
  // Floors is in the navigation for everyone; with no floors it offers to add the first one.
  await page.goto('/')
  const navigation = await openNavigation(page)
  await navigation.getByRole('link', { name: t('floors.nav'), exact: true }).click()
  await expect(page.getByRole('heading', { name: t('floors.empty.title') })).toBeVisible()
  await gates.check('/floors (empty)')

  await page.getByRole('button', { name: t('floors.empty.cta') }).click()
  const dialog = page.getByRole('dialog', { name: t('floors.form.createTitle') })
  await expect(dialog.getByLabel(t('floors.form.name'))).toHaveValue(t('floors.level.ground'))
  await gates.check('add floor dialog')
  await dialog.getByRole('button', { name: t('floors.form.create') }).click()
  await expect(page).toHaveURL(/\/floors\/\d+\/edit$/)
  const floorId = Number(/\/floors\/(\d+)\/edit/.exec(page.url())![1])
  await dismissToasts(page)

  // An empty floor explains how to start.
  await expect(page.getByRole('heading', { name: t('floors.editor.empty.title') })).toBeVisible()
  await gates.check('editor (empty floor)')
  await page.getByRole('button', { name: t('floors.editor.empty.start') }).click()

  // Four corners (clicks snap to the 50 cm grid), then the first corner again closes the outline.
  for (const [x, y] of [
    [100, 100],
    [600, 100],
    [600, 450],
    [100, 450],
  ] as const) {
    await clickPlan(page, x, y)
  }
  await clickPlan(page, 100, 100)
  await expect(roomLabel(page, `${t('floors.editor.roomBase')} 1`)).toBeVisible()
  if (!isPhone(page)) await expect(page.getByText(t('floors.editor.unsaved'))).toBeVisible()

  // Rename and recolor in the properties panel.
  let panel = await openPanel(page)
  const name = panel.getByLabel(t('floors.editor.room.name'), { exact: true })
  await expect(name).toHaveValue(`${t('floors.editor.roomBase')} 1`)
  await name.fill('Living room')
  const sage = panel.getByRole('button', { name: t('floors.editor.colors.room-3'), exact: true })
  await sage.click()
  await expect(sage).toHaveAttribute('aria-pressed', 'true')
  await gates.check('editor with a selected room')
  await closePanel(page)
  await expect(roomLabel(page, 'Living room')).toBeVisible()

  // Undo twice (color, then the name), redo twice.
  const undo = page.getByRole('toolbar').getByRole('button', { name: t('floors.editor.undo'), exact: true })
  const redo = page.getByRole('toolbar').getByRole('button', { name: t('floors.editor.redo'), exact: true })
  await undo.click()
  await undo.click()
  await expect(roomLabel(page, `${t('floors.editor.roomBase')} 1`)).toBeVisible()
  await redo.click()
  await expect(roomLabel(page, 'Living room')).toBeVisible()
  // The keyboard too: Ctrl+Z / Ctrl+Y.
  await page.keyboard.press('Control+z')
  await expect(roomLabel(page, `${t('floors.editor.roomBase')} 1`)).toBeVisible()
  await page.keyboard.press('Control+y')
  await expect(roomLabel(page, 'Living room')).toBeVisible()
  await redo.click()
  panel = await openPanel(page)
  await expect(
    panel.getByRole('button', { name: t('floors.editor.colors.room-3'), exact: true }),
  ).toHaveAttribute('aria-pressed', 'true')
  await closePanel(page)

  await page.getByRole('button', { name: new RegExp(`^${t('floors.editor.save')}`) }).click()
  await expect(page.getByText(t('floors.toast.saved'))).toBeVisible()
  if (!isPhone(page)) await expect(page.getByText(t('floors.editor.saved'))).toBeVisible()

  const saved = await apiGetFloor(page.request, floorId)
  expect(saved.rooms).toHaveLength(1)
  expect(saved.rooms[0]).toMatchObject({ name: 'Living room', colorToken: 'room-3' })
  expect(saved.rooms[0]!.polygon).toEqual([
    { x: 100, y: 100 },
    { x: 600, y: 100 },
    { x: 600, y: 450 },
    { x: 100, y: 450 },
  ])

  // Reloading shows what was saved.
  await page.reload()
  await expect(roomLabel(page, 'Living room')).toBeVisible()
  if (!isPhone(page)) await expect(page.getByText(t('floors.editor.saved'))).toBeVisible()
  await gates.check('editor after reload')

  // And the view page lists the room with its area (5 m × 3.5 m).
  await page.goto(`/floors/${floorId}`)
  await expect(page.getByRole('button', { name: /Living room\s*17\.5 m²/ })).toBeVisible()
})

test('an admin traces a plan image and calibrates it with two points', async ({ page, gates }) => {
  const floor = await apiCreateFloor(page.request)
  await page.goto(`/floors/${floor.id}/edit`)
  await page.getByRole('button', { name: t('floors.editor.empty.trace') }).click()

  const panel = await openPanel(page)
  await panel.locator('input[type="file"]').setInputFiles(PLAN_PNG)
  // 800 × 600 px fitted into 2000 × 1500 cm: 2.5 cm per pixel.
  await expect(panel.getByTestId('background-scale')).toHaveText('1 px = 2.5 cm')
  await expect(planSvg(page).locator('image')).toHaveCount(1)
  await gates.check('editor with a background image')

  await panel.getByRole('button', { name: t('floors.editor.background.calibrate') }).click()
  await closePanel(page)

  // Two points 250 cm apart on the plan as drawn now; their real distance is 500 cm.
  await clickPlan(page, 400, 600)
  await clickPlan(page, 650, 600)
  const dialog = page.getByRole('dialog', { name: t('floors.editor.calibration.title') })
  await expect(dialog).toBeVisible()
  await gates.check('calibration dialog')
  await dialog.getByRole('button', { name: t('floors.editor.calibration.centimeters'), exact: true }).click()
  await dialog.getByLabel(t('floors.editor.calibration.distance')).fill('500')
  await dialog.getByRole('button', { name: t('floors.editor.calibration.apply') }).click()
  await expect(dialog).toBeHidden()

  await page.getByRole('button', { name: new RegExp(`^${t('floors.editor.save')}`) }).click()
  await expect(page.getByText(t('floors.toast.saved'))).toBeVisible()

  const saved = await apiGetFloor(page.request, floor.id)
  expect(saved.background).not.toBeNull()
  // Twice the scale (5 cm per pixel), within what pixel-rounded clicks allow.
  expect(Math.abs(saved.background!.cmPerPx - 5) / 5).toBeLessThan(0.03)

  // The image is served by the API and shows in view mode too.
  const image = await page.request.get(`/api/files/${saved.background!.fileId}`)
  expect(image.headers()['content-type']).toBe('image/webp')
  await page.goto(`/floors/${floor.id}`)
  await expect(planSvg(page).locator('image')).toHaveCount(1)
})

test('saving over a newer version of the floor offers to reload it', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'Concurrency is the same on every screen size.')
  const floor = await apiCreateFloor(page.request)
  await page.goto(`/floors/${floor.id}/edit`)
  await page.getByRole('button', { name: t('floors.editor.empty.start') }).click()
  await page.keyboard.press('Escape') // back to Select: nothing drawn yet
  await page.keyboard.press('r')
  await clickPlan(page, 1000, 500)
  await clickPlan(page, 1400, 500)
  await clickPlan(page, 1400, 900)
  await page.keyboard.press('Enter')
  await expect(roomLabel(page, `${t('floors.editor.roomBase')} 1`)).toBeVisible()

  // Someone else saves the floor meanwhile.
  await apiSaveLayout(page.request, floor)

  await page.keyboard.press('Control+s')
  const conflict = page.getByRole('alertdialog')
  await expect(conflict).toContainText(t('floors.editor.conflict.title'))
  await conflict.getByRole('button', { name: t('floors.editor.conflict.reload') }).click()
  await expect(conflict).toBeHidden()
  await expect(roomLabel(page, 'Kitchen')).toBeVisible()
  await expect(roomLabel(page, `${t('floors.editor.roomBase')} 1`)).toHaveCount(0)
})

test('a member sees the plan in view mode only', async ({ page, gates }) => {
  const floor = await apiSaveLayout(page.request, await apiCreateFloor(page.request))
  await createUser(page.request, testData.member, 'Member')
  await signIn(page.request, testData.member)

  await page.goto('/')
  const navigation = await openNavigation(page)
  await navigation.getByRole('link', { name: t('floors.nav'), exact: true }).click()
  await expect(page.getByRole('link', { name: floor.name })).toBeVisible()
  await expect(page.getByRole('button', { name: t('floors.add') })).toHaveCount(0)
  await page.getByRole('link', { name: floor.name }).click()

  await expect(page).toHaveURL(new RegExp(`/floors/${floor.id}$`))
  await expect(roomLabel(page, 'Kitchen')).toBeVisible()
  await expect(page.getByRole('link', { name: t('floors.editPlan') })).toHaveCount(0)
  // Tapping a room on the plan (or in the list) highlights it.
  await page.getByRole('button', { name: /Kitchen\s*30 m²/ }).click()
  await expect(page.getByRole('button', { name: /Kitchen\s*30 m²/ })).toHaveAttribute('aria-pressed', 'true')
  await gates.check('/floors/:id as a member')

  // The editor is for admins.
  await page.goto(`/floors/${floor.id}/edit`)
  await expect(page).toHaveURL(/\/forbidden$/)
})
