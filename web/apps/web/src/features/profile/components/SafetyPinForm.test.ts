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

const ok = (data: unknown, status = 200) => ({
  data,
  error: undefined,
  response: new Response(null, { status }),
})

async function mountForm(locale: AppLocale = 'en') {
  const wrapper = mount(SafetyPinForm, {
    attachTo: document.body,
    global: { plugins: [createPinia(), createAppI18n({ locale })] },
  })
  await flushPromises()
  return wrapper
}

async function fill(wrapper: Awaited<ReturnType<typeof mountForm>>, values: string[]) {
  const inputs = wrapper.findAll('input[type="password"]')
  expect(inputs).toHaveLength(values.length)
  for (const [index, value] of values.entries()) await inputs[index]!.setValue(value)
}

beforeEach(() => vi.resetAllMocks())
afterEach(() => {
  document.body.innerHTML = ''
})

describe('SafetyPinForm', () => {
  it('sets a first PIN', async () => {
    api.getSafetyPin.mockResolvedValue(ok({ isSet: false, lockedForSeconds: null }))
    api.setSafetyPin.mockResolvedValue(ok(undefined, 204))
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('No PIN yet')
    await fill(wrapper, ['4821', '4821'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(api.setSafetyPin).toHaveBeenCalled())
    expect(api.setSafetyPin).toHaveBeenCalledWith({ body: { currentPin: null, newPin: '4821' } })
    wrapper.unmount()
  })

  it('checks the PIN format and the confirmation before sending', async () => {
    api.getSafetyPin.mockResolvedValue(ok({ isSet: false, lockedForSeconds: null }))
    const wrapper = await mountForm('pt-BR')
    await fill(wrapper, ['12a', '12a'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Use de 4 a 8 dígitos.'))
    await fill(wrapper, ['123456', '654321'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Os PINs não coincidem.'))
    expect(api.setSafetyPin).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('changes a PIN with the current one, and shows a wrong current PIN on its field', async () => {
    api.getSafetyPin.mockResolvedValue(ok({ isSet: true, lockedForSeconds: null }))
    api.setSafetyPin.mockResolvedValue({
      data: undefined,
      error: { status: 400, code: 'safety.pin_incorrect' },
      response: new Response(null, { status: 400 }),
    })
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('PIN set')
    await fill(wrapper, ['1111', '2222', '2222'])
    await wrapper.get('form').trigger('submit')
    await vi.waitFor(() => expect(wrapper.text()).toContain("That PIN isn't right."))
    expect(api.setSafetyPin).toHaveBeenCalledWith({ body: { currentPin: '1111', newPin: '2222' } })
    wrapper.unmount()
  })

  it('shows a lockout', async () => {
    api.getSafetyPin.mockResolvedValue(ok({ isSet: true, lockedForSeconds: 290 }))
    const wrapper = await mountForm()
    expect(wrapper.text()).toContain('Locked for 5 min after wrong PINs')
    wrapper.unmount()
  })
})
