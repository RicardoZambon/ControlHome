import {
  sendCommand,
  type CommandAcceptedDto,
  type CommandDefinitionDto,
  type EndpointCapabilityDto,
  type StateValue,
} from '@controlhome/api-client'
import { toast } from '@controlhome/ui'
import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSafetyPromptStore, type StepUpPrompt } from '../components/safety/safetyPrompt'
import { errorCodeOf, toErrorMessage, unwrap } from '../lib/problem'
import { asBool, toStateValue } from '../lib/stateValue'
import { useDevicesStore } from '../stores/devices'
import { useRealtimeStore, type CommandOutcome } from '../stores/realtime'
import { useSessionStore } from '../stores/session'

/** How a command ended for the control that sent it. */
export type CommandResult = CommandOutcome['status'] | 'rejected' | 'cancelled'

/** A command's timeout when its definition has none (the server's cloud default). */
export const DEFAULT_TIMEOUT_MS = 30_000
/** The client gives the server this much longer than the command's timeout before it stops waiting. */
export const DEADLINE_MARGIN_MS = 10_000

export interface SendOptions {
  /** The device's class: `cover.open` on a gate or garage is safety-critical. */
  deviceClass?: string | null
  /** What the PIN dialog asks to confirm: its title ("Unlock Front door lock?") and its button ("Unlock"). */
  prompt?: StepUpPrompt
}

/** Whether a command needs the safety PIN (always, or on this device class). */
export function isSafetyCritical(
  definition: Pick<CommandDefinitionDto, 'safetyCritical' | 'safetyCriticalForDeviceClasses'> | undefined,
  deviceClass?: string | null,
): boolean {
  if (!definition) return false
  return (
    definition.safetyCritical ||
    (!!deviceClass && definition.safetyCriticalForDeviceClasses.includes(deviceClass))
  )
}

/**
 * The values a command will most likely produce, shown while it is pending: arguments bound to attributes (`setLevel`
 * → `level`), plus the switch and media power commands. Safety-critical commands are never predicted.
 */
export function predictState(
  capability: Pick<EndpointCapabilityDto, 'capabilityId' | 'attributes'>,
  definition: Pick<CommandDefinitionDto, 'name' | 'arguments'> | undefined,
  command: string,
  args: Record<string, unknown> | undefined,
  read: (attributeId: number) => StateValue | undefined,
): Map<number, StateValue> {
  const ids = new Map(capability.attributes.map((attribute) => [attribute.key, attribute.attributeId]))
  const predicted = new Map<number, StateValue>()
  const set = (key: string, value: unknown) => {
    const id = ids.get(key)
    if (id !== undefined && value !== undefined) predicted.set(id, toStateValue(value))
  }

  if (capability.capabilityId === 'switch') {
    if (command === 'turnOn') set('on', true)
    else if (command === 'turnOff') set('on', false)
    else if (command === 'toggle') {
      const id = ids.get('on')
      const current = id === undefined ? undefined : asBool(read(id))
      if (current !== undefined) set('on', !current)
    }
  } else if (capability.capabilityId === 'mediaPlayer') {
    if (command === 'powerOn') set('power', true)
    else if (command === 'powerOff') set('power', false)
  }

  if (definition && args) {
    for (const argument of definition.arguments) {
      if (argument.key in args) set(argument.attribute ?? argument.key, args[argument.key])
    }
  }
  return predicted
}

/** The last request to each endpoint (settled or not), by endpoint id: see {@link inOrder}. */
const lastRequest = new Map<string, Promise<unknown>>()

/**
 * Runs `request` once the previous request to the same endpoint has been answered, so the server gets an endpoint's
 * commands in the order they were made. Two requests in flight together can arrive the other way round: two quick key
 * presses on a slider (51, then 52) would leave the light at 51.
 */
function inOrder<T>(endpointId: string, request: () => Promise<T>): Promise<T> {
  const next = (lastRequest.get(endpointId) ?? Promise.resolve()).then(request)
  const settled = next.catch(() => undefined)
  lastRequest.set(endpointId, settled)
  void settled.then(() => {
    if (lastRequest.get(endpointId) === settled) lastRequest.delete(endpointId)
  })
  return next
}

/** A new idempotency key (32 hex digits; `crypto.randomUUID` needs HTTPS, the home network often has plain HTTP). */
export function newIdempotencyKey(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  return [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('')
}

/**
 * Sends commands to one endpoint (`POST /api/endpoints/{id}/commands`) the way every control does:
 * - the predicted values show at once (optimistic) and stay until the device confirms; a failure puts the real ones back;
 * - an endpoint's requests go out one at a time, in the order they were made (the next waits for the previous answer),
 *   each within the command's deadline (an unanswered POST is aborted and reported as not confirmed, never re-sent);
 * - the outcome arrives over the hub (`CommandStatus`): failed, timed-out and (someone else's) superseded commands
 *   are reported in a toast;
 * - a safety-critical command answered with `safety.step_up_required` opens the PIN dialog (the command shows as
 *   waiting for the PIN meanwhile) and, once the PIN is accepted, is sent again — once — with the same idempotency key
 *   (at-most-once: the server remembers the key).
 */
export function useCommand(endpointId: MaybeRefOrGetter<string>) {
  const realtime = useRealtimeStore()
  const devices = useDevicesStore()
  const safety = useSafetyPromptStore()
  const session = useSessionStore()
  const { t, te } = useI18n()

  /** Members and Admins send commands; Viewers only look. */
  const canControl = computed(() => session.hasRole('Admin', 'Member'))

  function report(outcome: CommandOutcome): void {
    switch (outcome.status) {
      case 'failed': {
        const key =
          outcome.code && te(`errors.${outcome.code}`) ? `errors.${outcome.code}` : 'errors.commands.failed'
        toast.error(t(key))
        break
      }
      case 'timed_out':
        toast.warning(t('errors.commands.timed_out'))
        break
      case 'superseded':
        if (!outcome.replacedByOwn) toast.info(t('commands.superseded'))
        break
      default:
        break
    }
  }

  async function send(
    capability: EndpointCapabilityDto,
    command: string,
    args?: Record<string, unknown>,
    options: SendOptions = {},
  ): Promise<CommandResult> {
    if (!canControl.value) return 'rejected'
    const endpoint = toValue(endpointId)
    const definition = devices.definitionOf(capability)?.commands.find((entry) => entry.name === command)
    const critical = isSafetyCritical(definition, options.deviceClass)
    const key = newIdempotencyKey()
    realtime.beginCommand(
      { key, endpointId: endpoint, capabilityId: capability.capabilityId, command, args },
      critical ? new Map() : predictState(capability, definition, command, args, realtime.valueOf),
    )
    const deadline = (definition?.timeoutMs ?? DEFAULT_TIMEOUT_MS) + DEADLINE_MARGIN_MS

    let accepted: CommandAcceptedDto | undefined
    let steppedUp = false
    while (!accepted) {
      // The POST itself gets the same deadline (a server that never answers must not leave "Unlocking…" up forever).
      // Aborting it never re-sends anything: the command is reported as not confirmed, and its state comes live.
      const abort = new AbortController()
      const timer = setTimeout(() => abort.abort(), deadline)
      try {
        accepted = unwrap(
          await inOrder(endpoint, () =>
            sendCommand({
              path: { id: endpoint },
              body: {
                capabilityId: capability.capabilityId,
                command,
                arguments: args ?? null,
                idempotencyKey: key,
              },
              signal: abort.signal,
            }),
          ),
        )
      } catch (error) {
        if (abort.signal.aborted) {
          realtime.abandonCommand(key)
          toast.warning(t('errors.commands.timed_out'))
          return 'timed_out'
        }
        if (errorCodeOf(error) === 'safety.step_up_required' && !steppedUp) {
          steppedUp = true
          // Nothing has happened yet: the control says it waits for the PIN, not "Unlocking…".
          realtime.setAwaitingPin(key, true)
          const granted = await safety.requestStepUp(options.prompt)
          realtime.setAwaitingPin(key, false)
          if (granted) continue
          realtime.abandonCommand(key)
          return 'cancelled'
        }
        realtime.abandonCommand(key)
        toast.error(toErrorMessage(error, t, (name) => te(name)))
        return 'rejected'
      } finally {
        clearTimeout(timer)
      }
    }

    const outcome = await realtime.awaitCommand(key, accepted.commandId, deadline)
    report(outcome)
    return outcome.status
  }

  /** Whether a command of this browser is pending for the endpoint (optionally one capability or command). */
  function isPending(capabilityId?: string, command?: string): boolean {
    return realtime.isPending(toValue(endpointId), capabilityId, command)
  }

  /** The latest pending command of this browser for the endpoint (optionally one capability or command). */
  function pendingCommand(capabilityId?: string, command?: string) {
    return realtime.pendingFor(toValue(endpointId), capabilityId, command)
  }

  return { send, isPending, pendingCommand, canControl }
}

export type CommandSender = ReturnType<typeof useCommand>
