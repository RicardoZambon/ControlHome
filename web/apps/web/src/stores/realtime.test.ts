import type { AttributeStateDto, StateDelta } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { bool, num } from '../test/devices'
import {
  BANNER_DELAY_MS,
  SETTLE_MS,
  setHubConnectionFactory,
  useRealtimeStore,
  type HubConnectionLike,
} from './realtime'

const api = vi.hoisted(() => ({ getState: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

/** A hub connection driven by the test. */
class FakeHub implements HubConnectionLike {
  handlers = new Map<string, (...args: never[]) => void>()
  reconnecting?: () => void
  reconnected?: () => void
  closed?: () => void
  starts = 0
  stops = 0
  failStarts = 0

  on(method: string, handler: (...args: never[]) => void): void {
    this.handlers.set(method, handler)
  }
  onreconnecting(callback: () => void): void {
    this.reconnecting = callback
  }
  onreconnected(callback: () => void): void {
    this.reconnected = callback
  }
  onclose(callback: () => void): void {
    this.closed = callback
  }
  start(): Promise<void> {
    this.starts += 1
    if (this.failStarts > 0) {
      this.failStarts -= 1
      return Promise.reject(new Error('unreachable'))
    }
    return Promise.resolve()
  }
  stop(): Promise<void> {
    this.stops += 1
    return Promise.resolve()
  }
  /** A server → client message. */
  send(method: string, ...args: unknown[]): void {
    const handler = this.handlers.get(method) as ((...values: unknown[]) => void) | undefined
    handler?.(...args)
  }
}

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const state = (attributeId: number, value: AttributeStateDto['value'], at = '2026-10-12T10:00:00Z') => ({
  attributeId,
  value,
  observedAtUtc: at,
  lastChangedAtUtc: at,
  restored: false,
})
const delta = (attributeId: number, value: StateDelta['value'], at = '2026-10-12T10:00:05Z'): StateDelta => ({
  attributeId,
  value,
  observedAtUtc: at,
})

let hub: FakeHub

beforeEach(() => {
  vi.resetAllMocks()
  setActivePinia(createPinia())
  hub = new FakeHub()
  setHubConnectionFactory(() => hub)
  api.getState.mockResolvedValue(ok([state(1, bool(true)), state(2, num(80))]))
})

afterEach(() => {
  vi.useRealTimers()
})

describe('realtime store: connection and state', () => {
  it('connects, then fetches the whole state', async () => {
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    expect(realtime.status).toBe('connected')
    expect(api.getState).toHaveBeenCalledTimes(1)
    expect(realtime.valueOf(1)).toEqual(bool(true))
    expect(realtime.valueOf(2)).toEqual(num(80))
    // Starting again while running does nothing.
    await realtime.start()
    expect(hub.starts).toBe(1)
  })

  it('applies StateChanged batches, filling in the members the server left out, and ignores older values', async () => {
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    hub.send('StateChanged', [
      { attributeId: 1, value: { kind: 'bool', b: false }, observedAtUtc: '2026-10-12T10:00:05Z' },
      delta(3, num(21.5)),
    ])
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(realtime.valueOf(3)).toEqual(num(21.5))
    hub.send('StateChanged', [delta(1, bool(true), '2026-10-12T10:00:01Z')])
    expect(realtime.valueOf(1)).toEqual(bool(false))
  })

  it('a resync replaces everything, but keeps deltas that arrived while it was loading', async () => {
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    realtime.applyDeltas([delta(9, num(1))])

    let answer: (value: unknown) => void = () => {}
    api.getState.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)))
    const resync = realtime.resync()
    hub.send('StateChanged', [delta(2, num(55), '2026-10-12T10:00:10Z')])
    answer(ok([state(1, bool(false)), state(2, num(80))]))
    await resync
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(realtime.valueOf(2)).toEqual(num(55))
    // Gone from the server (e.g. its integration was removed).
    expect(realtime.valueOf(9)).toBeUndefined()
  })

  it('seeds values from device lists without overwriting newer ones', async () => {
    const realtime = useRealtimeStore()
    realtime.applyDeltas([delta(1, bool(false), '2026-10-12T10:00:05Z')])
    realtime.seed([
      { attributeId: 1, value: bool(true), observedAtUtc: '2026-10-12T10:00:00Z' },
      { attributeId: 4, value: num(3), observedAtUtc: null },
    ])
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(realtime.valueOf(4)).toEqual(num(3))
  })

  it('shows the banner after 3 s disconnected, then resyncs on reconnect and hides it', async () => {
    vi.useFakeTimers()
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    hub.reconnecting?.()
    expect(realtime.status).toBe('reconnecting')
    vi.advanceTimersByTime(BANNER_DELAY_MS - 1)
    expect(realtime.bannerVisible).toBe(false)
    vi.advanceTimersByTime(1)
    expect(realtime.bannerVisible).toBe(true)

    api.getState.mockResolvedValueOnce(ok([state(1, bool(false))]))
    hub.reconnected?.()
    await flushPromises()
    expect(realtime.status).toBe('connected')
    expect(realtime.bannerVisible).toBe(false)
    expect(api.getState).toHaveBeenCalledTimes(2)
    expect(realtime.valueOf(1)).toEqual(bool(false))
  })

  it('a short drop shows no banner', async () => {
    vi.useFakeTimers()
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    hub.reconnecting?.()
    vi.advanceTimersByTime(1_000)
    hub.reconnected?.()
    await flushPromises()
    vi.advanceTimersByTime(BANNER_DELAY_MS)
    expect(realtime.bannerVisible).toBe(false)
  })

  it('keeps trying when the first connection fails', async () => {
    vi.useFakeTimers()
    hub.failStarts = 2
    const realtime = useRealtimeStore()
    await realtime.start()
    expect(realtime.status).toBe('connecting')
    await vi.advanceTimersByTimeAsync(1_000)
    expect(hub.starts).toBe(2)
    await vi.advanceTimersByTimeAsync(2_000)
    expect(hub.starts).toBe(3)
    expect(realtime.status).toBe('connected')
    expect(api.getState).toHaveBeenCalledTimes(1)
  })

  it('starts over when the connection closes on its own, and stops cleanly', async () => {
    vi.useFakeTimers()
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    hub.closed?.()
    expect(realtime.status).toBe('reconnecting')
    await vi.advanceTimersByTimeAsync(1_000)
    expect(hub.starts).toBe(2)
    await realtime.stop()
    expect(hub.stops).toBe(1)
    expect(realtime.status).toBe('idle')
    // Messages of the stopped connection are ignored.
    hub.send('StateChanged', [delta(1, bool(false), '2026-10-12T11:00:00Z')])
    expect(realtime.valueOf(1)).toEqual(bool(true))
  })

  it('tracks availability, device list changes, integration and system status', async () => {
    const realtime = useRealtimeStore()
    await realtime.start()
    hub.send('AvailabilityChanged', { deviceId: 'd1', availability: 'Offline' })
    hub.send('DevicesChanged')
    hub.send('IntegrationStatusChanged', { id: 'i1', status: 'Degraded', message: 'slow' })
    hub.send('SystemStatusChanged', { runtimeMode: 'Standby', historyPaused: false, version: '1' })
    expect(realtime.availability.get('d1')).toBe('Offline')
    expect(realtime.devicesVersion).toBe(1)
    expect(realtime.integrationStatuses.get('i1')?.status).toBe('Degraded')
    expect(realtime.systemStatus?.runtimeMode).toBe('Standby')
  })
})

describe('realtime store: commands', () => {
  async function started() {
    const realtime = useRealtimeStore()
    await realtime.start()
    await flushPromises()
    return realtime
  }

  it('shows the predicted value while pending and until the confirming delta arrives', async () => {
    vi.useFakeTimers()
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'switch', command: 'turnOff' },
      new Map([[1, bool(false)]]),
    )
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(realtime.isPending('e1')).toBe(true)
    expect(realtime.isPending('e1', 'switch', 'turnOff')).toBe(true)
    expect(realtime.isPending('e1', 'level')).toBe(false)

    const outcome = realtime.awaitCommand('k1', 'c1', 10_000)
    // A periodic report of the old value while the command is in flight does not undo the prediction.
    hub.send('StateChanged', [delta(1, bool(true), '2026-10-12T10:00:01Z')])
    expect(realtime.valueOf(1)).toEqual(bool(false))

    hub.send('CommandStatus', { commandId: 'c1', status: 'pending', code: null })
    hub.send('CommandStatus', { commandId: 'c1', status: 'confirmed', code: null })
    await expect(outcome).resolves.toEqual({ status: 'confirmed', code: null })
    expect(realtime.isPending('e1')).toBe(false)
    // Confirmed, the delta not here yet: still the predicted value.
    expect(realtime.valueOf(1)).toEqual(bool(false))
    hub.send('StateChanged', [delta(1, bool(false), '2026-10-12T10:00:02Z')])
    expect(realtime.valueOf(1)).toEqual(bool(false))
    expect(realtime.liveOf(1)?.value).toEqual(bool(false))
  })

  it('drops a confirmed prediction after a while even without a delta (the value did not change)', async () => {
    vi.useFakeTimers()
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'level', command: 'setLevel' },
      new Map([[2, num(40)]]),
    )
    const outcome = realtime.awaitCommand('k1', 'c1', 10_000)
    hub.send('CommandStatus', { commandId: 'c1', status: 'confirmed', code: null })
    await outcome
    expect(realtime.valueOf(2)).toEqual(num(40))
    vi.advanceTimersByTime(SETTLE_MS)
    expect(realtime.valueOf(2)).toEqual(num(80))
  })

  it('puts the real value back when the command fails, with the failure code', async () => {
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'switch', command: 'turnOff' },
      new Map([[1, bool(false)]]),
    )
    const outcome = realtime.awaitCommand('k1', 'c1', 10_000)
    hub.send('CommandStatus', { commandId: 'c1', status: 'failed', code: 'commands.refused' })
    await expect(outcome).resolves.toEqual({ status: 'failed', code: 'commands.refused' })
    expect(realtime.valueOf(1)).toEqual(bool(true))
  })

  it('keeps a status that arrives before the POST answered', async () => {
    const realtime = await started()
    realtime.beginCommand({ key: 'k1', endpointId: 'e1', capabilityId: 'lock', command: 'lock' })
    hub.send('CommandStatus', { commandId: 'c1', status: 'confirmed', code: null })
    await expect(realtime.awaitCommand('k1', 'c1', 10_000)).resolves.toEqual({
      status: 'confirmed',
      code: null,
    })
  })

  it('times out on its own when no status arrives, and the prediction goes', async () => {
    vi.useFakeTimers()
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'switch', command: 'turnOff' },
      new Map([[1, bool(false)]]),
    )
    const outcome = realtime.awaitCommand('k1', 'c1', 5_000)
    vi.advanceTimersByTime(5_000)
    await expect(outcome).resolves.toEqual({ status: 'timed_out', code: 'commands.timed_out' })
    expect(realtime.valueOf(1)).toEqual(bool(true))
    expect(realtime.isPending('e1')).toBe(false)
  })

  it('reports superseded commands, and whether a later command of this browser replaced them', async () => {
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'level', command: 'setLevel' },
      new Map([[2, num(30)]]),
    )
    const first = realtime.awaitCommand('k1', 'c1', 10_000)
    realtime.beginCommand(
      { key: 'k2', endpointId: 'e1', capabilityId: 'level', command: 'setLevel' },
      new Map([[2, num(60)]]),
    )
    const second = realtime.awaitCommand('k2', 'c2', 10_000)
    hub.send('CommandStatus', { commandId: 'c1', status: 'superseded', code: null })
    await expect(first).resolves.toEqual({ status: 'superseded', code: null, replacedByOwn: true })
    // The later command's prediction stays.
    expect(realtime.valueOf(2)).toEqual(num(60))

    hub.send('CommandStatus', { commandId: 'c2', status: 'superseded', code: null })
    await expect(second).resolves.toEqual({ status: 'superseded', code: null, replacedByOwn: false })
  })

  it('forgets commands whose status may have been missed when the connection comes back', async () => {
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'switch', command: 'turnOff' },
      new Map([[1, bool(false)]]),
    )
    const sent = realtime.awaitCommand('k1', 'c1', 60_000)
    // Not yet sent (its POST is in flight): kept.
    realtime.beginCommand({ key: 'k2', endpointId: 'e2', capabilityId: 'switch', command: 'turnOn' })
    hub.reconnecting?.()
    hub.reconnected?.()
    await expect(sent).resolves.toEqual({ status: 'lost', code: null })
    await flushPromises()
    expect(realtime.isPending('e1')).toBe(false)
    expect(realtime.isPending('e2')).toBe(true)
    expect(realtime.valueOf(1)).toEqual(bool(true))
  })

  it('abandons a command that never reached the server', async () => {
    const realtime = await started()
    realtime.beginCommand(
      { key: 'k1', endpointId: 'e1', capabilityId: 'switch', command: 'turnOff' },
      new Map([[1, bool(false)]]),
    )
    realtime.abandonCommand('k1')
    expect(realtime.isPending('e1')).toBe(false)
    expect(realtime.valueOf(1)).toEqual(bool(true))
  })
})
