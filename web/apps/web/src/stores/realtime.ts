import {
  getState,
  type AttributeStateDto,
  type AvailabilityChangedDto,
  type CommandStatusDto,
  type IntegrationStatusChangedDto,
  type StateDelta,
  type StateValue,
  type SystemStatusDto,
} from '@controlhome/api-client'
import { HubConnectionBuilder, LogLevel, type ILogger } from '@microsoft/signalr'
import { defineStore } from 'pinia'
import { ref, shallowReactive } from 'vue'
import { unwrap } from '../lib/problem'

/**
 * Live state over SignalR (`/hubs/home`, docs/architecture.md §10 "Live updates"): the current value of every attribute
 * (by AttributeId), device availability, and the progress of the commands this browser sent.
 *
 * - The connection reconnects on its own, forever (backing off to 15 s), and starts again if the first attempt fails.
 * - On every (re)connect the whole state is fetched again (`GET /api/state`): deltas missed while disconnected are gone.
 * - Commands get optimistic values (shown until the device confirms or the command fails) and a client-side deadline,
 *   so nothing stays "pending" forever; a reconnect forgets commands whose outcome may have been missed.
 * - `bannerVisible` turns on once the connection has been down for {@link BANNER_DELAY_MS}.
 */

export const HUB_PATH = '/hubs/home'
/** How long the connection may be down before the "reconnecting" banner shows. */
export const BANNER_DELAY_MS = 3_000
/** Delays between reconnect attempts (the last one repeats). */
export const RETRY_DELAYS_MS = [0, 1_000, 2_000, 5_000, 10_000, 15_000] as const
/** After a confirmation, how long an optimistic value waits for the matching state delta. */
export const SETTLE_MS = 1_500
/** How long a command status is kept for a command whose POST has not answered yet. */
const STATUS_BUFFER_MS = 30_000

export type ConnectionStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting'

/** The current value of one attribute. */
export interface LiveValue {
  value: StateValue
  /** When the device observed it (`null`: unknown, e.g. never reported). */
  observedAtUtc: string | null
  /** Loaded from the database and not yet confirmed by the running connector. */
  restored: boolean
}

/** The parts of a SignalR `HubConnection` the store uses (tests pass a fake through {@link setHubConnectionFactory}). */
export interface HubConnectionLike {
  on(method: string, handler: (...args: never[]) => void): void
  onreconnecting(callback: (error?: Error) => void): void
  onreconnected(callback: (connectionId?: string) => void): void
  onclose(callback: (error?: Error) => void): void
  start(): Promise<void>
  stop(): Promise<void>
}

export type HubConnectionFactory = () => HubConnectionLike

/** SignalR's own log lines: warnings and errors as console warnings (a dropped connection is not an app error). */
const hubLogger: ILogger = {
  log(level: LogLevel, message: string) {
    if (level >= LogLevel.Warning) console.warn(`[realtime] ${message}`)
  },
}

function createSignalRConnection(): HubConnectionLike {
  return new HubConnectionBuilder()
    .withUrl(HUB_PATH, { headers: { 'X-ControlHome': '1' }, withCredentials: true })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (context) =>
        RETRY_DELAYS_MS[Math.min(context.previousRetryCount, RETRY_DELAYS_MS.length - 1)]!,
    })
    .configureLogging(hubLogger)
    .build()
}

let connectionFactory: HubConnectionFactory = createSignalRConnection

/** Replaces how the hub connection is created (tests); `undefined` restores SignalR. */
export function setHubConnectionFactory(factory?: HubConnectionFactory): void {
  connectionFactory = factory ?? createSignalRConnection
}

/** Final outcomes of a command, as `useCommand` sees them. */
export type CommandFinalStatus = 'confirmed' | 'failed' | 'timed_out' | 'superseded' | 'lost'

export interface CommandOutcome {
  status: CommandFinalStatus
  /** `commands.*` error code (failed, timed_out). */
  code: string | null
  /** Superseded by a later command this browser sent for the same control (no need to tell anyone). */
  replacedByOwn?: boolean
}

/** A command this browser sent and whose outcome is not known yet. */
export interface PendingCommand {
  /** The idempotency key: the command's identity before (and after) the server gives it an id. */
  key: string
  commandId?: string
  endpointId: string
  capabilityId: string
  command: string
  /** The command's arguments (for "Setting 22 °C…"). */
  args?: Record<string, unknown>
  /** A safety-critical command waiting for the person's safety PIN (nothing has been done yet). */
  awaitingPin?: boolean
  /** Order of sending (later commands have larger numbers). */
  sequence: number
  /** Attributes shown with an optimistic value while it is pending. */
  attributeIds: number[]
}

interface OptimisticValue {
  value: StateValue
  /** The command that set it. */
  key: string
  /** Confirmed: shown until the state delta arrives (or SETTLE_MS passes). */
  settling: boolean
}

const FINAL_STATUSES = new Set(['confirmed', 'failed', 'timed_out', 'superseded'])

/** A StateValue in its wire shape: `kind` plus only the member that is set (null members are dropped). */
export function normalizeState(value: Partial<StateValue> | null | undefined): StateValue {
  const state: StateValue = { kind: value?.kind ?? 'null' }
  if (value?.b != null) state.b = value.b
  if (value?.n != null) state.n = value.n
  if (value?.s != null) state.s = value.s
  if (value?.j != null) state.j = value.j
  return state
}

function isOlder(candidate: string | null | undefined, current: string | null | undefined): boolean {
  if (!candidate || !current) return !candidate && !!current
  const a = Date.parse(candidate)
  const b = Date.parse(current)
  return Number.isFinite(a) && Number.isFinite(b) && a < b
}

export const useRealtimeStore = defineStore('realtime', () => {
  const status = ref<ConnectionStatus>('idle')
  /** The connection has been down for at least BANNER_DELAY_MS. */
  const bannerVisible = ref(false)
  /** Current values by AttributeId. */
  const values = shallowReactive(new Map<number, LiveValue>())
  /** Availability changes since the device lists were loaded, by device id (`Online`, `Offline`, `Unknown`). */
  const availability = shallowReactive(new Map<string, string>())
  /** Latest integration status reports, by integration id. */
  const integrationStatuses = shallowReactive(new Map<string, IntegrationStatusChangedDto>())
  const systemStatus = ref<SystemStatusDto | null>(null)
  /** Bumped on every `DevicesChanged` (device lists reload when it changes). */
  const devicesVersion = ref(0)
  /** Bumped after every completed resync. */
  const resyncCount = ref(0)

  const optimistic = shallowReactive(new Map<number, OptimisticValue>())
  const pending = shallowReactive(new Map<string, PendingCommand>())

  let connection: HubConnectionLike | undefined
  /** Increases on every start/stop, so callbacks of an old connection are ignored. */
  let generation = 0
  let attempt = 0
  let retryTimer: ReturnType<typeof setTimeout> | undefined
  let bannerTimer: ReturnType<typeof setTimeout> | undefined
  let sequence = 0
  let resyncing: Promise<void> | undefined
  /** Attributes that got a delta while a resync was in flight (newer than the snapshot). */
  let touched: Set<number> | undefined

  const waiters = new Map<string, (outcome: CommandOutcome) => void>()
  const deadlines = new Map<string, ReturnType<typeof setTimeout>>()
  const keysByCommandId = new Map<string, string>()
  const statusBuffer = new Map<string, { status: string; code: string | null; at: number }>()
  const settleTimers = new Map<number, ReturnType<typeof setTimeout>>()

  // --- Connection ------------------------------------------------------------------------------------------------------

  function setStatus(next: ConnectionStatus): void {
    status.value = next
    if (next === 'connected' || next === 'idle') {
      clearTimeout(bannerTimer)
      bannerTimer = undefined
      bannerVisible.value = false
      return
    }
    if (bannerTimer === undefined && !bannerVisible.value) {
      bannerTimer = setTimeout(() => {
        bannerTimer = undefined
        if (status.value === 'connecting' || status.value === 'reconnecting') bannerVisible.value = true
      }, BANNER_DELAY_MS)
    }
  }

  /** Connects to the hub (once; later calls do nothing while it runs). */
  async function start(): Promise<void> {
    if (connection) return
    const current = connectionFactory()
    connection = current
    const owner = ++generation
    const live = () => owner === generation

    current.on('StateChanged', (deltas: StateDelta[]) => {
      if (live()) applyDeltas(deltas)
    })
    current.on('AvailabilityChanged', (change: AvailabilityChangedDto) => {
      if (live()) availability.set(change.deviceId, change.availability)
    })
    current.on('DevicesChanged', () => {
      if (live()) devicesVersion.value += 1
    })
    current.on('IntegrationStatusChanged', (change: IntegrationStatusChangedDto) => {
      if (live()) integrationStatuses.set(change.id, change)
    })
    current.on('CommandStatus', (change: CommandStatusDto) => {
      if (live()) handleCommandStatus(change)
    })
    current.on('SystemStatusChanged', (change: SystemStatusDto) => {
      if (live()) systemStatus.value = change
    })
    current.onreconnecting(() => {
      if (live()) setStatus('reconnecting')
    })
    current.onreconnected(() => {
      if (live()) void connected(true)
    })
    // Automatic reconnection never gives up, so a close we did not ask for (stop) is unexpected: start over.
    current.onclose(() => {
      if (!live()) return
      setStatus('reconnecting')
      scheduleConnect(current, owner)
    })

    setStatus('connecting')
    await connect(current, owner, false)
  }

  async function connect(current: HubConnectionLike, owner: number, again: boolean): Promise<void> {
    try {
      await current.start()
    } catch {
      if (owner === generation) scheduleConnect(current, owner)
      return
    }
    if (owner !== generation) return
    attempt = 0
    await connected(again)
  }

  function scheduleConnect(current: HubConnectionLike, owner: number): void {
    clearTimeout(retryTimer)
    const delay = RETRY_DELAYS_MS[Math.min(attempt + 1, RETRY_DELAYS_MS.length - 1)]!
    attempt += 1
    retryTimer = setTimeout(() => {
      retryTimer = undefined
      if (owner === generation) void connect(current, owner, true)
    }, delay)
  }

  async function connected(again: boolean): Promise<void> {
    setStatus('connected')
    // Statuses sent while we were away are lost; the fresh state shows how those commands ended.
    if (again) forgetSentCommands()
    await resync()
  }

  /** Disconnects (sign-out, leaving the signed-in layout) and forgets pending commands. */
  async function stop(): Promise<void> {
    generation += 1
    clearTimeout(retryTimer)
    retryTimer = undefined
    attempt = 0
    const current = connection
    connection = undefined
    setStatus('idle')
    forgetSentCommands()
    if (current) {
      try {
        await current.stop()
      } catch {
        // Already closed.
      }
    }
  }

  // --- State -------------------------------------------------------------------------------------------------------------

  function put(
    attributeId: number,
    value: Partial<StateValue>,
    observedAtUtc: string | null,
    restored: boolean,
  ) {
    const current = values.get(attributeId)
    if (current && isOlder(observedAtUtc, current.observedAtUtc)) return false
    values.set(attributeId, { value: normalizeState(value), observedAtUtc, restored })
    return true
  }

  /** Applies a `StateChanged` batch (an older value than the one shown is ignored). */
  function applyDeltas(deltas: readonly StateDelta[]): void {
    for (const delta of deltas) {
      if (!put(delta.attributeId, delta.value, delta.observedAtUtc ?? null, false)) continue
      touched?.add(delta.attributeId)
      const shown = optimistic.get(delta.attributeId)
      if (shown?.settling) dropOptimistic(delta.attributeId)
    }
  }

  /** Values from a device list or detail (`AttributeDto`), kept unless a newer one is already known. */
  function seed(
    attributes: readonly {
      attributeId: number
      value: Partial<StateValue>
      observedAtUtc: string | null
      restored?: boolean
    }[],
  ): void {
    for (const attribute of attributes) {
      if (attribute.observedAtUtc === null && values.has(attribute.attributeId)) continue
      put(attribute.attributeId, attribute.value, attribute.observedAtUtc, attribute.restored ?? false)
    }
  }

  function replaceAll(snapshot: readonly AttributeStateDto[]): void {
    const seen = new Set<number>()
    for (const entry of snapshot) {
      seen.add(entry.attributeId)
      if (touched?.has(entry.attributeId)) continue
      values.set(entry.attributeId, {
        value: normalizeState(entry.value),
        observedAtUtc: entry.observedAtUtc,
        restored: entry.restored,
      })
    }
    for (const attributeId of [...values.keys()]) {
      if (!seen.has(attributeId) && !touched?.has(attributeId)) values.delete(attributeId)
    }
  }

  /** `GET /api/state`: replaces every value (deltas that arrive meanwhile win). Failures keep what is shown. */
  function resync(): Promise<void> {
    resyncing ??= (async () => {
      touched = new Set()
      try {
        replaceAll(unwrap(await getState()))
        resyncCount.value += 1
      } catch {
        // Keep the current values; the next reconnect (or the device lists) brings fresh ones.
      } finally {
        touched = undefined
        resyncing = undefined
      }
    })()
    return resyncing
  }

  /** The value to show: the optimistic one while a command is pending, else the live one. */
  function valueOf(attributeId: number | undefined): StateValue | undefined {
    if (attributeId === undefined) return undefined
    return optimistic.get(attributeId)?.value ?? values.get(attributeId)?.value
  }

  function liveOf(attributeId: number | undefined): LiveValue | undefined {
    return attributeId === undefined ? undefined : values.get(attributeId)
  }

  // --- Commands ----------------------------------------------------------------------------------------------------------

  function dropOptimistic(attributeId: number): void {
    optimistic.delete(attributeId)
    clearTimeout(settleTimers.get(attributeId))
    settleTimers.delete(attributeId)
  }

  /** Registers a command about to be sent and shows its predicted values right away. */
  function beginCommand(
    command: Omit<PendingCommand, 'sequence' | 'attributeIds' | 'commandId'>,
    predicted: ReadonlyMap<number, StateValue> = new Map(),
  ): void {
    pending.set(command.key, { ...command, sequence: ++sequence, attributeIds: [...predicted.keys()] })
    for (const [attributeId, value] of predicted) {
      dropOptimistic(attributeId)
      optimistic.set(attributeId, { value: normalizeState(value), key: command.key, settling: false })
    }
  }

  /**
   * The server accepted the command (202 with its id): resolves with its final status, or `timed_out` once
   * `deadlineMs` passes without one (the hub may have missed it).
   */
  function awaitCommand(key: string, commandId: string, deadlineMs: number): Promise<CommandOutcome> {
    const entry = pending.get(key)
    if (!entry) return Promise.resolve({ status: 'lost', code: null })
    pending.set(key, { ...entry, commandId })
    keysByCommandId.set(commandId, key)
    return new Promise<CommandOutcome>((resolve) => {
      waiters.set(key, resolve)
      deadlines.set(
        key,
        setTimeout(() => finishCommand(key, { status: 'timed_out', code: 'commands.timed_out' }), deadlineMs),
      )
      const early = statusBuffer.get(commandId)
      statusBuffer.delete(commandId)
      if (early && FINAL_STATUSES.has(early.status)) finishWith(key, early.status, early.code)
    })
  }

  /** The command never reached the server (refused, cancelled): its optimistic values go. */
  function abandonCommand(key: string): void {
    finishCommand(key, { status: 'lost', code: null })
  }

  function finishWith(key: string, status: string, code: string | null): void {
    const entry = pending.get(key)
    if (!entry) return
    const outcome: CommandOutcome = { status: status as CommandFinalStatus, code }
    if (status === 'superseded') {
      outcome.replacedByOwn = [...pending.values()].some(
        (other) =>
          other.key !== key &&
          other.endpointId === entry.endpointId &&
          other.capabilityId === entry.capabilityId &&
          other.sequence > entry.sequence,
      )
    }
    finishCommand(key, outcome)
  }

  function finishCommand(key: string, outcome: CommandOutcome): void {
    const entry = pending.get(key)
    if (!entry) return
    pending.delete(key)
    clearTimeout(deadlines.get(key))
    deadlines.delete(key)
    if (entry.commandId) keysByCommandId.delete(entry.commandId)
    for (const attributeId of entry.attributeIds) {
      const shown = optimistic.get(attributeId)
      if (!shown || shown.key !== key) continue
      if (outcome.status === 'confirmed') {
        optimistic.set(attributeId, { ...shown, settling: true })
        clearTimeout(settleTimers.get(attributeId))
        settleTimers.set(
          attributeId,
          setTimeout(() => {
            if (optimistic.get(attributeId)?.key === key) dropOptimistic(attributeId)
          }, SETTLE_MS),
        )
      } else {
        dropOptimistic(attributeId)
      }
    }
    const resolve = waiters.get(key)
    waiters.delete(key)
    resolve?.(outcome)
  }

  function handleCommandStatus(change: CommandStatusDto): void {
    if (!FINAL_STATUSES.has(change.status)) return
    const key = keysByCommandId.get(change.commandId)
    if (key) {
      finishWith(key, change.status, change.code ?? null)
      return
    }
    // The hub can be faster than the POST's answer: keep it for awaitCommand.
    const now = Date.now()
    for (const [id, entry] of statusBuffer) if (now - entry.at > STATUS_BUFFER_MS) statusBuffer.delete(id)
    statusBuffer.set(change.commandId, { status: change.status, code: change.code ?? null, at: now })
  }

  /** Forgets every command that reached the server (its status may have been missed): no stale "pending" remains. */
  function forgetSentCommands(): void {
    for (const entry of [...pending.values()]) {
      if (entry.commandId) finishCommand(entry.key, { status: 'lost', code: null })
    }
  }

  /** Marks a pending safety-critical command as waiting for the safety PIN (or not any more). */
  function setAwaitingPin(key: string, awaiting: boolean): void {
    const entry = pending.get(key)
    if (entry && entry.awaitingPin !== awaiting) pending.set(key, { ...entry, awaitingPin: awaiting })
  }

  /** The latest pending command of this browser for the endpoint (optionally one capability or command). */
  function pendingFor(
    endpointId: string,
    capabilityId?: string,
    command?: string,
  ): PendingCommand | undefined {
    let latest: PendingCommand | undefined
    for (const entry of pending.values()) {
      if (entry.endpointId !== endpointId) continue
      if (capabilityId !== undefined && entry.capabilityId !== capabilityId) continue
      if (command !== undefined && entry.command !== command) continue
      if (!latest || entry.sequence > latest.sequence) latest = entry
    }
    return latest
  }

  /** Whether a command of this browser is pending for the endpoint (optionally one capability or command). */
  function isPending(endpointId: string, capabilityId?: string, command?: string): boolean {
    return pendingFor(endpointId, capabilityId, command) !== undefined
  }

  /** Clears everything (sign-out). */
  function reset(): void {
    values.clear()
    availability.clear()
    integrationStatuses.clear()
    for (const attributeId of [...optimistic.keys()]) dropOptimistic(attributeId)
    systemStatus.value = null
  }

  return {
    status,
    bannerVisible,
    values,
    availability,
    integrationStatuses,
    systemStatus,
    devicesVersion,
    resyncCount,
    pending,
    start,
    stop,
    resync,
    applyDeltas,
    seed,
    valueOf,
    liveOf,
    beginCommand,
    awaitCommand,
    abandonCommand,
    setAwaitingPin,
    pendingFor,
    isPending,
    reset,
  }
})
