import type { EndpointCapabilityDto, MeDto } from '@controlhome/api-client'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { useSafetyPromptStore } from '../components/safety/safetyPrompt'
import { createAppI18n } from '../i18n'
import { useDevicesStore } from '../stores/devices'
import { setHubConnectionFactory, useRealtimeStore, type HubConnectionLike } from '../stores/realtime'
import { useSessionStore } from '../stores/session'
import { CATALOGUE, basicHome, bool, num } from '../test/devices'
import {
  DEADLINE_MARGIN_MS,
  DEFAULT_TIMEOUT_MS,
  isSafetyCritical,
  newIdempotencyKey,
  predictState,
  useCommand,
} from './useCommand'

const api = vi.hoisted(() => ({ sendCommand: vi.fn(), getState: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const toast = vi.hoisted(() =>
  Object.assign(vi.fn(), { error: vi.fn(), warning: vi.fn(), info: vi.fn(), success: vi.fn() }),
)
vi.mock('@controlhome/ui', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/ui')>()),
  toast,
}))

/** The hub, so tests can send `CommandStatus`. */
const hub = {
  handlers: new Map<string, (value: unknown) => void>(),
  send(method: string, value: unknown) {
    this.handlers.get(method)?.(value)
  },
}
function fakeConnection(): HubConnectionLike {
  return {
    on: (method, handler) => hub.handlers.set(method, handler as (value: unknown) => void),
    onreconnecting: () => {},
    onreconnected: () => {},
    onclose: () => {},
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
  }
}

const accepted = (commandId: string) => ({
  data: { commandId },
  error: undefined,
  response: new Response(null, { status: 202 }),
})
const problem = (status: number, code: string) => ({
  data: undefined,
  error: { status, code },
  response: new Response(null, { status }),
})

let pinia: Pinia

async function setup(role: 'Admin' | 'Member' | 'Viewer' = 'Member', endpointId = 'e-light') {
  const me: MeDto = {
    id: 'u1',
    userName: 'grace',
    displayName: 'Grace',
    roles: [role],
    locale: 'en',
    theme: 'light',
  }
  useSessionStore().me = me
  const devices = useDevicesStore()
  devices.capabilities = new Map(CATALOGUE.map((capability) => [capability.id, capability]))
  const home = basicHome()
  devices.devices = home
  // Connect first: the resync on connect replaces every value (the mocked server has none).
  await useRealtimeStore().start()
  await flushPromises()
  devices.seedValues(home)

  let sender!: ReturnType<typeof useCommand>
  mount(
    defineComponent({
      setup() {
        sender = useCommand(endpointId)
        return () => null
      },
    }),
    { global: { plugins: [pinia, createAppI18n({ locale: 'en' })] } },
  )
  return { sender, home, realtime: useRealtimeStore() }
}

const capabilityOf = (home: ReturnType<typeof basicHome>, deviceId: string, capabilityId: string) =>
  home
    .find((device) => device.id === deviceId)!
    .endpoints[0]!.capabilities.find((capability) => capability.capabilityId === capabilityId)!

beforeEach(() => {
  vi.resetAllMocks()
  pinia = createPinia()
  setActivePinia(pinia)
  hub.handlers.clear()
  setHubConnectionFactory(fakeConnection)
  api.getState.mockResolvedValue({
    data: [],
    error: undefined,
    response: new Response(null, { status: 200 }),
  })
})

afterEach(() => {
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('useCommand', () => {
  it('shows the predicted value while pending, then the confirmation', async () => {
    const { sender, home, realtime } = await setup()
    api.sendCommand.mockResolvedValue(accepted('c1'))
    const result = sender.send(capabilityOf(home, 'd-light', 'switch'), 'turnOff')
    // Optimistic: off at once, and pending.
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(sender.isPending()).toBe(true)
    expect(sender.isPending('switch', 'turnOff')).toBe(true)
    await flushPromises()

    const body = api.sendCommand.mock.calls[0]![0].body
    expect(api.sendCommand.mock.calls[0]![0].path).toEqual({ id: 'e-light' })
    expect(body).toMatchObject({ capabilityId: 'switch', command: 'turnOff', arguments: null })
    expect(body.idempotencyKey).toMatch(/^[0-9a-f]{32}$/)

    hub.send('CommandStatus', { commandId: 'c1', status: 'confirmed', code: null })
    await expect(result).resolves.toBe('confirmed')
    expect(sender.isPending()).toBe(false)
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('times out on its own after the command timeout and says so', async () => {
    vi.useFakeTimers()
    const { sender, home, realtime } = await setup()
    api.sendCommand.mockResolvedValue(accepted('c1'))
    const result = sender.send(capabilityOf(home, 'd-light', 'level'), 'setLevel', { level: 40 })
    await flushPromises()
    expect(realtime.valueOf(2)).toEqual(num(40))
    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS + DEADLINE_MARGIN_MS - 1)
    expect(sender.isPending()).toBe(true)
    await vi.advanceTimersByTimeAsync(1)
    await expect(result).resolves.toBe('timed_out')
    expect(toast.warning).toHaveBeenCalledWith("The device didn't confirm in time. Check its state.")
    // The real value is back.
    expect(realtime.valueOf(2)).toEqual(num(80))
  })

  it('reports a failure with its translated code', async () => {
    const { sender, home, realtime } = await setup()
    api.sendCommand.mockResolvedValue(accepted('c1'))
    const result = sender.send(capabilityOf(home, 'd-light', 'switch'), 'turnOff')
    await flushPromises()
    hub.send('CommandStatus', { commandId: 'c1', status: 'failed', code: 'commands.refused' })
    await expect(result).resolves.toBe('failed')
    expect(toast.error).toHaveBeenCalledWith('The device refused the action.')
    expect(realtime.valueOf(1)).toEqual(bool(true))
  })

  it('tells when someone else replaced the change, not when this browser did', async () => {
    const { sender, home } = await setup()
    api.sendCommand.mockResolvedValueOnce(accepted('c1')).mockResolvedValueOnce(accepted('c2'))
    const level = capabilityOf(home, 'd-light', 'level')
    const first = sender.send(level, 'setLevel', { level: 30 })
    await flushPromises()
    const second = sender.send(level, 'setLevel', { level: 60 })
    await flushPromises()
    hub.send('CommandStatus', { commandId: 'c1', status: 'superseded', code: null })
    await expect(first).resolves.toBe('superseded')
    expect(toast.info).not.toHaveBeenCalled()
    hub.send('CommandStatus', { commandId: 'c2', status: 'superseded', code: null })
    await expect(second).resolves.toBe('superseded')
    expect(toast.info).toHaveBeenCalledWith('Someone else changed this device just now.')
  })

  it('sends quick commands to an endpoint one at a time, in the order they were made', async () => {
    // Two key presses on a slider: if both requests were in flight together, the server could get 52 before 51 and
    // leave the light at 51.
    const { sender, home, realtime } = await setup()
    let answerFirst!: (value: unknown) => void
    api.sendCommand
      .mockReturnValueOnce(new Promise((resolve) => (answerFirst = resolve)))
      .mockResolvedValueOnce(accepted('c2'))
    const level = capabilityOf(home, 'd-light', 'level')
    const first = sender.send(level, 'setLevel', { level: 51 })
    const second = sender.send(level, 'setLevel', { level: 52 })
    await flushPromises()
    // Both show at once (the latest wins); the second request waits until the server has taken the first.
    expect(realtime.valueOf(2)).toEqual(num(52))
    expect(api.sendCommand).toHaveBeenCalledTimes(1)
    expect(api.sendCommand.mock.calls[0]![0].body.arguments).toEqual({ level: 51 })

    answerFirst(accepted('c1'))
    await flushPromises()
    expect(api.sendCommand).toHaveBeenCalledTimes(2)
    expect(api.sendCommand.mock.calls[1]![0].body.arguments).toEqual({ level: 52 })
    hub.send('CommandStatus', { commandId: 'c1', status: 'superseded', code: null })
    hub.send('CommandStatus', { commandId: 'c2', status: 'confirmed', code: null })
    await expect(first).resolves.toBe('superseded')
    await expect(second).resolves.toBe('confirmed')
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('a refused request does not hold up the next one', async () => {
    const { sender, home } = await setup()
    api.sendCommand
      .mockResolvedValueOnce(problem(400, 'commands.invalid_arguments'))
      .mockResolvedValueOnce(accepted('c2'))
    const level = capabilityOf(home, 'd-light', 'level')
    const first = sender.send(level, 'setLevel', { level: 0 })
    const second = sender.send(level, 'setLevel', { level: 40 })
    await expect(first).resolves.toBe('rejected')
    await flushPromises()
    expect(api.sendCommand).toHaveBeenCalledTimes(2)
    hub.send('CommandStatus', { commandId: 'c2', status: 'confirmed', code: null })
    await expect(second).resolves.toBe('confirmed')
  })

  it('a refused request puts the value back and shows why', async () => {
    const { sender, home, realtime } = await setup()
    api.sendCommand.mockResolvedValue(problem(400, 'commands.invalid_arguments'))
    await expect(sender.send(capabilityOf(home, 'd-light', 'level'), 'setLevel', { level: 0 })).resolves.toBe(
      'rejected',
    )
    expect(toast.error).toHaveBeenCalledWith("The device can't take that value.")
    expect(realtime.valueOf(2)).toEqual(num(80))
    expect(sender.isPending()).toBe(false)
  })

  it('asks for the safety PIN when the server wants a step-up, then sends again once with the same key', async () => {
    const { sender, home, realtime } = await setup('Member', 'e-lock')
    const lock = capabilityOf(home, 'd-lock', 'lock')
    api.sendCommand
      .mockResolvedValueOnce(problem(403, 'safety.step_up_required'))
      .mockResolvedValueOnce(accepted('c9'))
    const result = sender.send(lock, 'unlock', undefined, { actionLabel: 'Unlock · Front door lock' })
    await flushPromises()

    const safety = useSafetyPromptStore()
    expect(safety.prompt).toEqual({ action: 'Unlock · Front door lock' })
    // Never predicted (at-most-once): the lock still shows locked, and the control waits.
    expect(realtime.valueOf(14)).toEqual({ kind: 'string', b: null, n: null, s: 'locked', j: null })
    expect(realtime.isPending('e-lock', 'lock')).toBe(true)

    safety.complete(true)
    await flushPromises()
    expect(api.sendCommand).toHaveBeenCalledTimes(2)
    const [first, second] = api.sendCommand.mock.calls.map((call) => call[0].body)
    expect(second.idempotencyKey).toBe(first.idempotencyKey)
    hub.send('CommandStatus', { commandId: 'c9', status: 'confirmed', code: null })
    await expect(result).resolves.toBe('confirmed')
  })

  it('retries only once: a second step-up request is an error', async () => {
    const { sender, home } = await setup('Member', 'e-lock')
    api.sendCommand.mockResolvedValue(problem(403, 'safety.step_up_required'))
    const result = sender.send(capabilityOf(home, 'd-lock', 'lock'), 'unlock')
    await flushPromises()
    useSafetyPromptStore().complete(true)
    await expect(result).resolves.toBe('rejected')
    expect(api.sendCommand).toHaveBeenCalledTimes(2)
    expect(toast.error).toHaveBeenCalledWith('Confirm with your safety PIN.')
  })

  it('cancelling the PIN dialog cancels the command', async () => {
    const { sender, home, realtime } = await setup('Member', 'e-lock')
    api.sendCommand.mockResolvedValue(problem(403, 'safety.step_up_required'))
    const result = sender.send(capabilityOf(home, 'd-lock', 'lock'), 'unlock')
    await flushPromises()
    useSafetyPromptStore().complete(false)
    await expect(result).resolves.toBe('cancelled')
    expect(api.sendCommand).toHaveBeenCalledTimes(1)
    expect(realtime.isPending('e-lock')).toBe(false)
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('Viewers send nothing', async () => {
    const { sender, home } = await setup('Viewer')
    expect(sender.canControl.value).toBe(false)
    await expect(sender.send(capabilityOf(home, 'd-light', 'switch'), 'turnOn')).resolves.toBe('rejected')
    expect(api.sendCommand).not.toHaveBeenCalled()
  })
})

describe('predictState', () => {
  const capability = (capabilityId: string, keys: string[]): EndpointCapabilityDto => ({
    id: 1,
    capabilityId,
    capabilityVersion: 1,
    retired: false,
    constraints: null,
    genericDefinition: null,
    attributes: keys.map((key, index) => ({
      attributeId: index + 1,
      key,
      value: { kind: 'null', b: null, n: null, s: null, j: null },
      observedAtUtc: null,
      lastChangedAtUtc: null,
      restored: false,
    })),
  })
  const definitionOf = (id: string, name: string) =>
    CATALOGUE.find((entry) => entry.id === id)!.commands.find((command) => command.name === name)

  it('predicts switch commands, toggle from the current value', () => {
    const switchCapability = capability('switch', ['on'])
    expect(
      predictState(switchCapability, definitionOf('switch', 'turnOn'), 'turnOn', undefined, () => undefined),
    ).toEqual(new Map([[1, bool(true)]]))
    expect(
      predictState(switchCapability, definitionOf('switch', 'toggle'), 'toggle', undefined, () => bool(true)),
    ).toEqual(new Map([[1, bool(false)]]))
    expect(predictState(switchCapability, undefined, 'toggle', undefined, () => undefined).size).toBe(0)
  })

  it('predicts the attributes that arguments are bound to', () => {
    const thermostat = capability('thermostat', ['mode', 'targetTemperature'])
    expect(
      predictState(
        thermostat,
        definitionOf('thermostat', 'setTargetTemperature'),
        'setTargetTemperature',
        { targetTemperature: 21.5 },
        () => undefined,
      ),
    ).toEqual(new Map([[2, num(21.5)]]))
    expect(
      predictState(
        capability('lock', ['lockState']),
        definitionOf('lock', 'unlock'),
        'unlock',
        {},
        () => undefined,
      ).size,
    ).toBe(0)
  })
})

describe('helpers', () => {
  it('knows the safety-critical commands, including per device class', () => {
    expect(isSafetyCritical({ safetyCritical: true, safetyCriticalForDeviceClasses: [] })).toBe(true)
    const coverOpen = { safetyCritical: false, safetyCriticalForDeviceClasses: ['gate', 'garage'] }
    expect(isSafetyCritical(coverOpen, 'gate')).toBe(true)
    expect(isSafetyCritical(coverOpen, 'blind')).toBe(false)
    expect(isSafetyCritical(undefined)).toBe(false)
  })

  it('makes unique 32-hex idempotency keys without crypto.randomUUID (plain HTTP)', () => {
    const keys = new Set(Array.from({ length: 50 }, newIdempotencyKey))
    expect(keys.size).toBe(50)
    for (const key of keys) expect(key).toMatch(/^[0-9a-f]{32}$/)
  })
})
