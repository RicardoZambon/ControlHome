import { RouterLinkStub, flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../../i18n'
import SafetyPinDialog from './SafetyPinDialog.vue'
import { useSafetyPromptStore } from './safetyPrompt'

const api = vi.hoisted(() => ({ stepUp: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const noContent = { data: undefined, error: undefined, response: new Response(null, { status: 204 }) }
const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => ({
  data: undefined,
  error: { status, code, ...extra },
  response: new Response(null, { status }),
})

let pinia: Pinia

function mountDialog() {
  return mount(SafetyPinDialog, {
    attachTo: document.body,
    global: { plugins: [pinia, createAppI18n({ locale: 'en' })], stubs: { RouterLink: RouterLinkStub } },
  })
}

const dialog = () => document.querySelector<HTMLElement>('[data-slot="safety-pin-dialog"]')
const filledDots = () =>
  [...document.querySelectorAll('[data-slot="pin-dots"] span')].filter((dot) =>
    dot.className.includes('bg-primary'),
  ).length

/** Presses one key on the dialog (Enter, Backspace, a digit). */
function press(key: string): void {
  dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }))
}

/** Types digits on the keyboard. */
function type(digits: string): void {
  for (const digit of digits) press(digit)
}

function button(text: string): HTMLButtonElement {
  return [...dialog()!.querySelectorAll('button')].find((entry) => entry.textContent?.trim() === text)!
}

beforeEach(() => {
  vi.resetAllMocks()
  pinia = createPinia()
  setActivePinia(pinia)
})

afterEach(() => {
  document.body.innerHTML = ''
})

describe('SafetyPinDialog', () => {
  it('opens for a step-up, takes the PIN from the keyboard and grants on success', async () => {
    const wrapper = mountDialog()
    const store = useSafetyPromptStore()
    const granted = store.requestStepUp({ action: 'Unlock · Front door lock' })
    await flushPromises()
    expect(dialog()?.textContent).toContain('Safety PIN')
    expect(dialog()?.textContent).toContain('Enter your safety PIN to confirm: Unlock · Front door lock.')

    type('12')
    await flushPromises()
    expect(filledDots()).toBe(2)
    expect(button('Confirm').disabled).toBe(true)
    type('345')
    press('Backspace')
    await flushPromises()
    expect(filledDots()).toBe(4)

    api.stepUp.mockResolvedValue(noContent)
    press('Enter')
    await expect(granted).resolves.toBe(true)
    expect(api.stepUp).toHaveBeenCalledWith({ body: { pin: '1234' } })
    await flushPromises()
    expect(store.prompt).toBeNull()
    wrapper.unmount()
  })

  it('says when the PIN is wrong, clears it, and lets the person try again with the pad', async () => {
    const wrapper = mountDialog()
    const granted = useSafetyPromptStore().requestStepUp()
    await flushPromises()
    api.stepUp.mockResolvedValueOnce(problem(400, 'safety.pin_incorrect'))
    type('0000')
    await flushPromises()
    button('Confirm').click()
    await flushPromises()
    expect(dialog()!.querySelector('[data-slot="pin-error"]')?.textContent?.trim()).toBe(
      "That PIN isn't right.",
    )
    expect(filledDots()).toBe(0)

    api.stepUp.mockResolvedValueOnce(noContent)
    for (const digit of '2468') button(digit).click()
    await flushPromises()
    button('Confirm').click()
    await expect(granted).resolves.toBe(true)
    expect(api.stepUp).toHaveBeenLastCalledWith({ body: { pin: '2468' } })
    wrapper.unmount()
  })

  it('locks the pad after too many wrong PINs', async () => {
    const wrapper = mountDialog()
    void useSafetyPromptStore().requestStepUp()
    await flushPromises()
    api.stepUp.mockResolvedValue(problem(423, 'safety.locked', { retryAfterSeconds: 600 }))
    type('9999')
    press('Enter')
    await flushPromises()
    expect(dialog()!.querySelector('[data-slot="pin-error"]')?.textContent).toContain('Try again in 10 min.')
    expect(button('5').disabled).toBe(true)
    wrapper.unmount()
  })

  it('points to the profile when no PIN is set', async () => {
    const wrapper = mountDialog()
    const granted = useSafetyPromptStore().requestStepUp()
    await flushPromises()
    api.stepUp.mockResolvedValue(problem(409, 'safety.pin_not_set'))
    type('1234')
    press('Enter')
    await flushPromises()
    expect(dialog()?.textContent).toContain('Set a safety PIN in your profile first.')
    expect(wrapper.findComponent(RouterLinkStub).props('to')).toEqual({
      name: 'profile',
      hash: '#safety-pin',
    })
    button('Cancel').click()
    await expect(granted).resolves.toBe(false)
    wrapper.unmount()
  })

  it('Cancel declines the step-up', async () => {
    const wrapper = mountDialog()
    const granted = useSafetyPromptStore().requestStepUp()
    await flushPromises()
    button('Cancel').click()
    await expect(granted).resolves.toBe(false)
    expect(api.stepUp).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
