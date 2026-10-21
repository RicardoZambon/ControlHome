import {
  cancelIntegrationFlow,
  continueIntegrationFlow,
  type FieldSchemaDto,
  type FlowStepDto,
} from '@controlhome/api-client'
import { getCurrentInstance, onBeforeUnmount, ref, shallowRef } from 'vue'
import {
  defaultValueFor,
  fieldsFromFieldSchemas,
  localized,
  type SchemaFieldDefinition,
} from '../../components/schema/schema'
import { unwrap } from '../../lib/problem'

/**
 * Setup and re-authorization flows (docs/architecture.md §4 "Flow runtime"): the server answers each step with a flat
 * `FlowStepDto` (`form` | `externalUrl` | `progress` | `choose` | `done` | `abort`); the wizard renders it and sends the
 * answer. Pure helpers here, and `useIntegrationFlow`, which drives one flow (polling progress steps).
 */

export type FlowStepType = 'form' | 'externalUrl' | 'progress' | 'choose' | 'done' | 'abort'

const KNOWN_TYPES: readonly string[] = ['form', 'externalUrl', 'progress', 'choose', 'done', 'abort']

/** The step's type; an unknown one (a newer server) is treated as an abort. */
export function stepType(step: Pick<FlowStepDto, 'type'>): FlowStepType {
  return KNOWN_TYPES.includes(step.type) ? (step.type as FlowStepType) : 'abort'
}

/** The flow is over: nothing more to send (the server already forgot it). */
export function isFinal(step: Pick<FlowStepDto, 'type'>): boolean {
  const type = stepType(step)
  return type === 'done' || type === 'abort'
}

/** A form step's fields for `<SchemaForm>`, in the UI language. */
export function formFields(step: Pick<FlowStepDto, 'fields'>, locale: string): SchemaFieldDefinition[] {
  return fieldsFromFieldSchemas(step.fields, locale)
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * A form step's starting values: what the server sent back (never secrets), else a default per field (a required
 * field gets its schema's default, an optional one stays empty). Secret fields always start empty.
 */
export function formValues(step: Pick<FlowStepDto, 'fields' | 'values'>): Record<string, unknown> {
  const sent = isObject(step.values) ? step.values : {}
  const values: Record<string, unknown> = {}
  for (const field of step.fields) {
    if (field.secret) values[field.key] = ''
    else if (field.key in sent && sent[field.key] !== null) values[field.key] = sent[field.key]
    else values[field.key] = initialValue(field)
  }
  return values
}

function initialValue(field: FieldSchemaDto): unknown {
  if (field.schema.type === 'bool') return false
  if (!field.required) return field.schema.type === 'flags' ? [] : ''
  return defaultValueFor(field.schema)
}

/** The server's messages for a rejected form, by field key, in the UI language. */
export function formErrors(step: Pick<FlowStepDto, 'errors'>, locale: string): Record<string, string> {
  const errors: Record<string, string> = {}
  for (const [key, text] of Object.entries(step.errors ?? {})) errors[key] = localized(text, locale)
  return errors
}

/** Messages for keys that are not a field of the form (shown above it). */
export function orphanErrors(step: Pick<FlowStepDto, 'errors' | 'fields'>, locale: string): string[] {
  const keys = new Set(step.fields.map((field) => field.key))
  return Object.entries(step.errors ?? {})
    .filter(([key]) => !keys.has(key))
    .map(([, text]) => localized(text, locale))
}

/** The answer to an external-page step: the pasted value under its key, or `{}` when nothing is pasted back. */
export function pasteBackInput(
  step: Pick<FlowStepDto, 'pasteBack'>,
  value: unknown,
): Record<string, unknown> {
  return step.pasteBack ? { [step.pasteBack.key]: value } : {}
}

/** The answer to a choose step. */
export function chooseInput(choice: string): { choice: string } {
  return { choice }
}

/** The external page to open, only when it is an `https:` URL (anything else is never rendered as a link). */
export function safeExternalUrl(url: string | null | undefined): string | null {
  if (!url) return null
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' ? parsed.href : null
  } catch {
    return null
  }
}

/** Whole seconds left until `endsAt` (never negative). */
export function secondsLeft(endsAt: number | null, now: number): number | null {
  if (endsAt === null) return null
  return Math.max(0, Math.ceil((endsAt - now) / 1000))
}

/** How often a progress step is polled. */
export const PROGRESS_POLL_MS = 1_000

export interface FlowApi {
  continueFlow: (flowId: string, input: unknown) => Promise<FlowStepDto>
  cancelFlow: (flowId: string) => Promise<void>
}

const defaultApi: FlowApi = {
  continueFlow: async (flowId, input) =>
    unwrap(await continueIntegrationFlow({ path: { flowId }, body: { input: input ?? {} } })),
  cancelFlow: async (flowId) => {
    unwrap(await cancelIntegrationFlow({ path: { flowId } }))
  },
}

export interface IntegrationFlowOptions {
  /** Starts the flow (`POST /api/integrations/flows` or `…/{id}/flows/reauth`). */
  start: () => Promise<FlowStepDto>
  api?: FlowApi
  pollMs?: number
  /** The clock (tests). */
  now?: () => number
}

/**
 * Drives one flow: `begin()` starts it, `answer(input)` sends the current step's answer, progress steps are polled with
 * `{}` until the step changes (one request at a time), and `cancel()` abandons it. A flow left half-way (the page is
 * closed) is cancelled on the server too. A request that fails (the flow expired, the network) sets `error` and keeps
 * the step, so the person can retry or start again.
 */
export function useIntegrationFlow(options: IntegrationFlowOptions) {
  const api = options.api ?? defaultApi
  const pollMs = options.pollMs ?? PROGRESS_POLL_MS
  const now = options.now ?? (() => Date.now())

  const step = shallowRef<FlowStepDto | null>(null)
  const busy = ref(false)
  const error = ref<unknown>(null)
  /** Steps answered so far (a "step 2" counter). */
  const count = ref(0)
  /** When the current progress step gives up (client clock). */
  const progressEndsAt = ref<number | null>(null)
  let timer: ReturnType<typeof setTimeout> | undefined
  let disposed = false

  function show(next: FlowStepDto): void {
    const changed = step.value?.type !== next.type || step.value?.stepId !== next.stepId
    step.value = next
    if (changed) count.value += 1
    if (stepType(next) === 'progress') {
      if (changed || progressEndsAt.value === null)
        progressEndsAt.value = next.timeoutSeconds !== null ? now() + next.timeoutSeconds * 1000 : null
      schedulePoll()
    } else {
      progressEndsAt.value = null
    }
  }

  function schedulePoll(): void {
    clearTimeout(timer)
    timer = setTimeout(() => {
      timer = undefined
      if (!disposed && step.value && stepType(step.value) === 'progress' && !busy.value) void send({}, true)
    }, pollMs)
  }

  async function send(input: unknown, polling = false): Promise<void> {
    const current = step.value
    if (!current || isFinal(current) || busy.value) return
    if (!polling) busy.value = true
    if (!polling) error.value = null
    try {
      const next = await api.continueFlow(current.flowId, input)
      if (disposed) return
      error.value = null
      show(next)
    } catch (failure) {
      if (disposed) return
      error.value = failure
      // A poll that failed for the network tries again; an expired flow (404) stops.
      if (polling && step.value && stepType(step.value) === 'progress' && !isGone(failure)) schedulePoll()
    } finally {
      if (!polling) busy.value = false
    }
  }

  function isGone(failure: unknown): boolean {
    return (
      typeof failure === 'object' &&
      failure !== null &&
      'status' in failure &&
      (failure as { status?: number }).status === 404
    )
  }

  async function begin(): Promise<void> {
    clearTimeout(timer)
    step.value = null
    count.value = 0
    error.value = null
    busy.value = true
    try {
      const first = await options.start()
      if (!disposed) show(first)
    } catch (failure) {
      if (!disposed) error.value = failure
    } finally {
      busy.value = false
    }
  }

  /** Sends the answer to the current step. */
  function answer(input: unknown): Promise<void> {
    clearTimeout(timer)
    return send(input)
  }

  /** Abandons the flow on the server (nothing happens for a finished one). */
  async function cancel(): Promise<void> {
    clearTimeout(timer)
    const current = step.value
    step.value = null
    if (!current || isFinal(current)) return
    try {
      await api.cancelFlow(current.flowId)
    } catch {
      // Already gone (expired): nothing to cancel.
    }
  }

  /** Stops polling and abandons an unfinished flow (the component goes away). */
  function dispose(): void {
    disposed = true
    const current = step.value
    clearTimeout(timer)
    if (current && !isFinal(current)) void api.cancelFlow(current.flowId).catch(() => undefined)
  }

  if (getCurrentInstance()) onBeforeUnmount(dispose)

  return { step, busy, error, count, progressEndsAt, begin, answer, cancel, dispose }
}
