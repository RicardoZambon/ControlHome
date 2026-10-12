import {
  completeSetup,
  createUser,
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

test('an admin can neither deactivate nor demote their own, last admin account', async ({ page, gates }) => {
  await page.goto('/users')
  // The list shows the status; changing it is a menu action, and your own account says why it can't.
  await expect(
    page.getByText(t('users.active'), { exact: true }).filter({ visible: true }).first(),
  ).toBeVisible()
  await page.getByRole('button', { name: t('users.actions.menu', { name: admin.displayName }) }).click()
  const deactivate = page.getByRole('menuitem', { name: new RegExp(`^${t('users.actions.deactivate')}`) })
  await expect(deactivate).toBeDisabled()
  await expect(deactivate).toContainText(t('users.selfDeactivate'))
  await gates.check('/users with the menu of your own account')
  await page.keyboard.press('Escape')

  // Demoting through the edit dialog is refused by the server (users.last_admin); its switch can't deactivate you.
  await page.getByRole('button', { name: t('users.actions.menu', { name: admin.displayName }) }).click()
  await page.getByRole('menuitem', { name: t('users.actions.edit') }).click()
  const dialog = page.getByRole('dialog', { name: t('users.form.editTitle', { name: admin.displayName }) })
  await expect(dialog.getByRole('switch')).toBeDisabled()
  await dialog.getByRole('radio', { name: new RegExp(`^${t('roles.Member')}`) }).check()
  await dialog.getByRole('button', { name: t('users.form.save') }).click()
  await expect(dialog.getByRole('alert')).toContainText(t('errors.users.last_admin'))
  await gates.check('edit dialog with the last-admin error')
})

test('deactivating and reactivating an account asks first', async ({ page, gates }) => {
  await createUser(page.request, member, 'Member')
  await page.goto('/users')
  await expect(page.getByText(member.userName, { exact: true }).filter({ visible: true })).toBeVisible()

  const menu = page.getByRole('button', { name: t('users.actions.menu', { name: member.displayName }) })
  await menu.click()
  await page.getByRole('menuitem', { name: t('users.actions.deactivate') }).click()
  const confirm = page.getByRole('alertdialog', {
    name: t('users.deactivate.title', { name: member.displayName }),
  })
  await expect(confirm).toBeVisible()
  await gates.check('deactivate confirmation')

  // Cancel changes nothing.
  await confirm.getByRole('button', { name: t('common.cancel') }).click()
  await expect(confirm).toBeHidden()
  await expect(page.getByText(t('users.inactive'), { exact: true }).filter({ visible: true })).toHaveCount(0)

  await menu.click()
  await page.getByRole('menuitem', { name: t('users.actions.deactivate') }).click()
  await confirm.getByRole('button', { name: t('users.deactivate.confirm') }).click()
  await expect(confirm).toBeHidden()
  await expect(page.getByText(t('users.toast.disabled', { name: member.displayName }))).toBeVisible()
  await expect(page.getByText(t('users.inactive'), { exact: true }).filter({ visible: true })).toBeVisible()
  await gates.check('/users with an inactive member')
  await dismissToasts(page)

  await menu.click()
  await page.getByRole('menuitem', { name: t('users.actions.reactivate') }).click()
  const reactivate = page.getByRole('alertdialog', {
    name: t('users.reactivate.title', { name: member.displayName }),
  })
  await reactivate.getByRole('button', { name: t('users.reactivate.confirm') }).click()
  await expect(page.getByText(t('users.toast.enabled', { name: member.displayName }))).toBeVisible()
  await expect(page.getByText(t('users.inactive'), { exact: true }).filter({ visible: true })).toHaveCount(0)
})
