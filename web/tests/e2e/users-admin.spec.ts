import {
  completeSetup,
  dismissToasts,
  expect,
  openNavigation,
  resetApp,
  signInWithForm,
  signOutFromMenu,
  test,
  testData,
} from './fixtures'
import { t } from './i18n'

const { admin, member } = testData

test.beforeEach(async ({ page }) => {
  await resetApp(page.request)
  // Through the page's request context: the browser is signed in as the admin.
  await completeSetup(page.request)
})

test('an admin adds a member, who does not get the Users page', async ({ page, gates }) => {
  await page.goto('/')
  const adminNavigation = await openNavigation(page)
  await adminNavigation.getByRole('link', { name: t('users.nav'), exact: true }).click()
  await expect(page).toHaveURL(/\/users$/)
  await expect(page.getByRole('heading', { level: 1, name: t('users.title') })).toBeVisible()
  await gates.check('/users')

  await page.getByRole('button', { name: t('users.add') }).click()
  const dialog = page.getByRole('dialog', { name: t('users.form.createTitle') })
  await expect(dialog).toBeVisible()
  await dialog.getByLabel(t('users.form.displayName'), { exact: true }).fill(member.displayName)
  await dialog.getByLabel(t('users.form.userName'), { exact: true }).fill(member.userName)
  await dialog.getByLabel(t('users.form.password'), { exact: true }).fill(member.password)
  await expect(dialog.getByRole('radio', { name: new RegExp(`^${t('roles.Member')}`) })).toBeChecked()
  await gates.check('add user dialog')
  await dialog.getByRole('button', { name: t('users.form.create') }).click()

  await expect(dialog).toBeHidden()
  await expect(page.getByText(t('users.toast.created', { name: member.displayName }))).toBeVisible()
  // The table on wide screens, cards on phones: only one of them is shown.
  await expect(page.getByText(member.userName, { exact: true }).filter({ visible: true })).toBeVisible()
  await gates.check('/users with a member')

  await signOutFromMenu(page, admin.displayName)
  await signInWithForm(page, member)
  await expect(page).toHaveURL(/\/$/)

  const memberNavigation = await openNavigation(page)
  await expect(memberNavigation.getByRole('link', { name: t('dashboard.nav'), exact: true })).toBeVisible()
  await expect(memberNavigation.getByRole('link', { name: t('profile.nav'), exact: true })).toBeVisible()
  await expect(memberNavigation.getByRole('link', { name: t('users.nav'), exact: true })).toHaveCount(0)
  await gates.check('member navigation')

  // Typing the address does not help either.
  await page.goto('/users')
  await expect(page).toHaveURL(/\/forbidden$/)
  await expect(page.getByRole('heading', { name: t('system.forbidden.heading') })).toBeVisible()
})

test('the last active admin can be neither disabled nor demoted', async ({ page, gates }) => {
  await page.goto('/users')
  const active = page.getByRole('switch', { name: t('users.activeToggle', { name: admin.displayName }) })
  await expect(active).toBeChecked()

  await active.click()
  await expect(page.getByText(t('errors.users.last_admin'))).toBeVisible()
  await expect(active).toBeChecked()
  await gates.check('/users after the last-admin guard')
  // On phones the toast would sit on top of the dialog's buttons.
  await dismissToasts(page)

  // Demoting through the edit dialog is refused with the same message.
  await page.getByRole('button', { name: t('users.actions.menu', { name: admin.displayName }) }).click()
  await page.getByRole('menuitem', { name: t('users.actions.edit') }).click()
  const dialog = page.getByRole('dialog', { name: t('users.form.editTitle', { name: admin.displayName }) })
  await dialog.getByRole('radio', { name: new RegExp(`^${t('roles.Member')}`) }).check()
  await dialog.getByRole('button', { name: t('users.form.save') }).click()
  await expect(dialog.getByRole('alert')).toContainText(t('errors.users.last_admin'))
  await gates.check('edit dialog with the last-admin error')
})
