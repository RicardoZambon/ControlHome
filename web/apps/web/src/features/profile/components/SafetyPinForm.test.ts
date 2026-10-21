import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type AppLocale } from '../../../i18n'
import SafetyPinForm from './SafetyPinForm.vue'

const api = vi.hoisted(() => ({ getSafetyPin: vi.fn(), setSafetyPin: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const PASSWORD = 'analytical engine notes'

const ok = (data: unknown, status = 200) => ({
  data,
  error: undefined,
  response: new Response(null, { status }),
})
const problem = (status: number, code: string) => ({
  data: undefined,
  error: { status, code },
  response: new Response(null, { status }),
})
const noPin = ok({ isSet: false, lockedForSeconds: null })
const pinSet = ok({ isSet: true, lockedForSeconds: null })

async function mountForm(locale: AppLocale = 'en') {
  const wrapper = mount(SafetyPinForm, {
    attachTo: document.body,
    global: { plugins: [createPinia(), createAppI18n({ locale })] },
  })
  await flushPromises()
  return wrapper
}
type Wrapper = Awaited<ReturnType<typeof mountForm>>

/** Fills the form's secret fields in order: the proof (account password or current PIN), the new PIN, the confirmation. */
async function fill(wrapper: Wrapper, values: string[]) {
  const inputs = wrapper.findAll('input[type="password"]')
  expect(inputs).toHaveLength(values.length)
  for (const [index, value] of values.entries()) await inputs[index]!.setValue(value)
}

/** The form item (label, input, messages) of the field labelled `label`. */
function item(wrapper: Wrapper, label: string) {
  const found = wrapper
    .findAll('[data-slot="form-item"]')
    .find((entry) => entry.find('label').exists() && entry.get('label').text() === label)
  expect(found, `no field labelled "${label}"`).toBeDefined()
  return found!
}

beforeEach(() => vi.resetAllMocks())
afterEach(() => {
  document.body.innerHTML = ''
})

describe('SafetyPinForm', () => {
  it('sets a first PIN with the account password', async () => {
    api.getSafetyPin.mockResolvedValue(noPin)
    api.setSafetyPin.mockResolvedValue(ok(undefined, 204))
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('No PIN yet')
    // No current PIN to ask for, and no "forgot" path: the account password is the proof.
    expect(item(wrapper, 'Account password').get('input').attributes('autocomplete')).toBe('current-password')
    expect(wrapper.find('[data-slot="pin-forgot"]').exists()).toBe(false)
    expect(wrapper.find('[data-slot="pin-use-current"]').exists()).toBe(false)
    await fill(wrapper, [PASSWORD, '4821', '4821'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(api.setSafetyPin).toHaveBeenCalled())
    expect(api.setSafetyPin).toHaveBeenCalledWith({
      body: { currentPin: null, currentPassword: PASSWORD, newPin: '4821' },
    })
    wrapper.unmount()
  })

  it('needs the account password before sending a first PIN', async () => {
    api.getSafetyPin.mockResolvedValue(noPin)
    const wrapper = await mountForm()
    await fill(wrapper, ['', '4821', '4821'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() =>
      expect(item(wrapper, 'Account password').text()).toContain('This field is required.'),
    )
    expect(api.setSafetyPin).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('checks the PIN format and the confirmation before sending', async () => {
    api.getSafetyPin.mockResolvedValue(noPin)
    const wrapper = await mountForm('pt-BR')
    expect(item(wrapper, 'Senha da conta').exists()).toBe(true)
    await fill(wrapper, [PASSWORD, '12a', '12a'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Use de 4 a 8 dígitos.'))
    await fill(wrapper, [PASSWORD, '123456', '654321'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Os PINs não coincidem.'))
    expect(api.setSafetyPin).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows a wrong account password on its field', async () => {
    api.getSafetyPin.mockResolvedValue(noPin)
    api.setSafetyPin.mockResolvedValue(problem(400, 'auth.password_incorrect'))
    const wrapper = await mountForm()
    await fill(wrapper, ['not my password', '4821', '4821'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() =>
      expect(item(wrapper, 'Account password').text()).toContain("Your current password isn't right."),
    )
    expect(item(wrapper, 'Account password').get('input').attributes('aria-invalid')).toBe('true')
    expect(wrapper.find('[data-slot="alert"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('changes a PIN with the current one, and shows a wrong current PIN on its field', async () => {
    api.getSafetyPin.mockResolvedValue(pinSet)
    api.setSafetyPin.mockResolvedValue(problem(400, 'safety.pin_incorrect'))
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('PIN set')
    expect(wrapper.text()).not.toContain('Account password')
    await fill(wrapper, ['1111', '2222', '2222'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(item(wrapper, 'Current PIN').text()).toContain("That PIN isn't right."))
    expect(api.setSafetyPin).toHaveBeenCalledWith({
      body: { currentPin: '1111', currentPassword: null, newPin: '2222' },
    })
    wrapper.unmount()
  })

  it('replaces a forgotten PIN with the account password, and can go back to the current PIN', async () => {
    api.getSafetyPin.mockResolvedValue(pinSet)
    api.setSafetyPin.mockResolvedValue(ok(undefined, 204))
    const wrapper = await mountForm()
    await wrapper.get('[data-slot="pin-forgot"]').trigger('click')
    await flushPromises()
    // The account password takes the current PIN's place, with the cursor in it.
    const password = item(wrapper, 'Account password').get('input')
    expect(document.activeElement).toBe(password.element)
    expect(wrapper.text()).not.toContain('Current PIN')

    // Back to the current PIN, then forgotten again.
    await wrapper.get('[data-slot="pin-use-current"]').trigger('click')
    await flushPromises()
    expect(document.activeElement).toBe(item(wrapper, 'Current PIN').get('input').element)
    await wrapper.get('[data-slot="pin-forgot"]').trigger('click')
    await flushPromises()

    await fill(wrapper, [PASSWORD, '2580', '2580'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(api.setSafetyPin).toHaveBeenCalled())
    expect(api.setSafetyPin).toHaveBeenCalledWith({
      body: { currentPin: null, currentPassword: PASSWORD, newPin: '2580' },
    })
    // Done: the form is back to "change with the current PIN".
    await vi.waitFor(() => expect(item(wrapper, 'Current PIN').exists()).toBe(true))
    wrapper.unmount()
  })

  it('asks for the account password when the PIN was removed meanwhile', async () => {
    // The page still thinks a PIN is set, but an administrator reset it: the server wants the account password.
    api.getSafetyPin.mockResolvedValueOnce(pinSet).mockResolvedValue(noPin)
    api.setSafetyPin.mockResolvedValue(problem(400, 'safety.password_required'))
    const wrapper = await mountForm()
    await fill(wrapper, ['1111', '2222', '2222'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() =>
      expect(item(wrapper, 'Account password').text()).toContain(
        'Enter your account password to set a new safety PIN.',
      ),
    )
    expect(wrapper.text()).toContain('No PIN yet')
    expect(api.getSafetyPin).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('shows a lockout', async () => {
    api.getSafetyPin.mockResolvedValue(ok({ isSet: true, lockedForSeconds: 290 }))
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('Locked for 5 min after wrong PINs')
    wrapper.unmount()
  })
})
