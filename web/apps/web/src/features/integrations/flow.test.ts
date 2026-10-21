import type { FieldSchemaDto, FlowStepDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../lib/problem'
import {
  chooseInput,
  formErrors,
  formFields,
  formValues,
  isFinal,
  orphanErrors,
  pasteBackInput,
  safeExternalUrl,
  secondsLeft,
  stepType,
  useIntegrationFlow,
  type FlowApi,
} from './flow'

const text = (en: string, ptBr = en) => ({ en, ptBr })

const FIELDS: FieldSchemaDto[] = [
  {
    key: 'preset',
    label: text('Preset', 'Conjunto'),
    schema: { type: 'enum', values: ['basic', 'demo'] },
    required: true,
    secret: false,
    advanced: false,
    help: null,
  },
  {
    key: 'seed',
    label: text('Seed'),
    schema: { type: 'number', min: 0, integer: true },
    required: false,
    secret: false,
    advanced: true,
    help: text('Same seed, same home.', 'Mesma semente, mesma casa.'),
  },
  {
    key: 'simulateReauth',
    label: text('Expire'),
    schema: { type: 'bool' },
    required: false,
    secret: false,
    advanced: false,
    help: null,
  },
  {
    key: 'token',
    label: text('Token'),
    schema: { type: 'string' },
    required: true,
    secret: true,
    advanced: false,
    help: null,
  },
]

function step(type: string, extra: Partial<FlowStepDto> = {}): FlowStepDto {
  return {
    flowId: 'flow-1',
    type,
    stepId: type,
    title: null,
    description: null,
    fields: [],
    values: null,
    errors: {},
    url: null,
    pasteBack: null,
    timeoutSeconds: null,
    choices: [],
    integrationId: null,
    reason: null,
    message: null,
    ...extra,
  }
}

describe('flow steps', () => {
  it('knows the step types and which ones end the flow', () => {
    expect(stepType(step('form'))).toBe('form')
    expect(stepType(step('somethingNew'))).toBe('abort')
    expect(isFinal(step('done'))).toBe(true)
    expect(isFinal(step('abort'))).toBe(true)
    expect(isFinal(step('progress'))).toBe(false)
  })

  it('builds the form fields in the UI language', () => {
    const fields = formFields(step('form', { fields: FIELDS }), 'pt-BR')
    expect(fields.map((field) => field.label)).toEqual(['Conjunto', 'Seed', 'Expire', 'Token'])
    expect(fields[1]!.help).toBe('Mesma semente, mesma casa.')
    expect(fields.map((field) => field.advanced)).toEqual([false, true, false, false])
    expect(fields[3]!.secret).toBe(true)
  })

  it('starts from the values sent back, defaults otherwise, never a secret', () => {
    expect(formValues(step('form', { fields: FIELDS, values: { seed: 7, token: 'leak?' } }))).toEqual({
      preset: 'basic',
      seed: 7,
      simulateReauth: false,
      token: '',
    })
    expect(formValues(step('form', { fields: FIELDS }))).toMatchObject({ seed: '' })
  })

  it('translates field errors and lists the ones that belong to no field', () => {
    const rejected = step('form', {
      fields: FIELDS,
      errors: { seed: text('Too big.', 'Grande demais.'), other: text('Hub busy.', 'Hub ocupado.') },
    })
    expect(formErrors(rejected, 'pt-BR')).toEqual({ seed: 'Grande demais.', other: 'Hub ocupado.' })
    expect(orphanErrors(rejected, 'en')).toEqual(['Hub busy.'])
  })

  it('builds the answers of external-page and choose steps', () => {
    const paste: FieldSchemaDto = { ...FIELDS[3]!, key: 'code' }
    expect(pasteBackInput(step('externalUrl', { pasteBack: paste }), 'abc')).toEqual({ code: 'abc' })
    expect(pasteBackInput(step('externalUrl'), 'ignored')).toEqual({})
    expect(chooseInput('hub-2')).toEqual({ choice: 'hub-2' })
  })

  it('only opens https pages', () => {
    expect(safeExternalUrl('https://vendor.example/authorize?x=1')).toBe(
      'https://vendor.example/authorize?x=1',
    )
    expect(safeExternalUrl('http://vendor.example')).toBeNull()
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull()
    expect(safeExternalUrl('not a url')).toBeNull()
    expect(safeExternalUrl(null)).toBeNull()
  })

  it('counts the seconds left', () => {
    expect(secondsLeft(10_500, 10_000)).toBe(1)
    expect(secondsLeft(10_000, 12_000)).toBe(0)
    expect(secondsLeft(null, 0)).toBeNull()
  })
})

describe('useIntegrationFlow', () => {
  let api: { continueFlow: ReturnType<typeof vi.fn>; cancelFlow: ReturnType<typeof vi.fn> }
  let clock = 0

  beforeEach(() => {
    vi.useFakeTimers()
    clock = 1_000
    api = { continueFlow: vi.fn(), cancelFlow: vi.fn().mockResolvedValue(undefined) }
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  function create(first: FlowStepDto | Error) {
    return useIntegrationFlow({
      start: () => (first instanceof Error ? Promise.reject(first) : Promise.resolve(first)),
      api: api as unknown as FlowApi,
      pollMs: 1_000,
      now: () => clock,
    })
  }

  it('answers a form and polls the progress step until it is done', async () => {
    const flow = create(step('form', { fields: FIELDS }))
    await flow.begin()
    expect(flow.step.value?.type).toBe('form')
    expect(flow.count.value).toBe(1)

    api.continueFlow.mockResolvedValueOnce(step('progress', { timeoutSeconds: 60 }))
    await flow.answer({ preset: 'demo' })
    expect(api.continueFlow).toHaveBeenLastCalledWith('flow-1', { preset: 'demo' })
    expect(flow.step.value?.type).toBe('progress')
    expect(flow.progressEndsAt.value).toBe(61_000)
    expect(flow.count.value).toBe(2)

    // Still waiting: the same step again (the deadline stays), then done.
    api.continueFlow.mockResolvedValueOnce(step('progress', { timeoutSeconds: 59 }))
    clock = 2_000
    await vi.advanceTimersByTimeAsync(1_000)
    expect(api.continueFlow).toHaveBeenLastCalledWith('flow-1', {})
    expect(flow.progressEndsAt.value).toBe(61_000)
    expect(flow.count.value).toBe(2)

    api.continueFlow.mockResolvedValueOnce(step('done', { integrationId: 'int-9' }))
    await vi.advanceTimersByTimeAsync(1_000)
    expect(flow.step.value?.integrationId).toBe('int-9')
    expect(flow.progressEndsAt.value).toBeNull()

    // Done: no more polls, and nothing to cancel.
    await vi.advanceTimersByTimeAsync(5_000)
    expect(api.continueFlow).toHaveBeenCalledTimes(3)
    await flow.cancel()
    expect(api.cancelFlow).not.toHaveBeenCalled()
  })

  it('keeps the step and reports the error when an answer fails', async () => {
    const flow = create(step('form', { fields: FIELDS }))
    await flow.begin()
    api.continueFlow.mockRejectedValueOnce(new ApiError({ code: 'flows.not_found', status: 404 }, 404))
    await flow.answer({})
    expect(flow.step.value?.type).toBe('form')
    expect((flow.error.value as ApiError).problem?.code).toBe('flows.not_found')
    expect(flow.busy.value).toBe(false)
  })

  it('stops polling an expired flow, retries after a network failure', async () => {
    const flow = create(step('progress', { timeoutSeconds: 30 }))
    await flow.begin()
    api.continueFlow.mockRejectedValueOnce(new TypeError('offline'))
    await vi.advanceTimersByTimeAsync(1_000)
    expect(flow.error.value).toBeInstanceOf(TypeError)
    api.continueFlow.mockRejectedValueOnce(new ApiError({ code: 'flows.not_found', status: 404 }, 404))
    await vi.advanceTimersByTimeAsync(1_000)
    await vi.advanceTimersByTimeAsync(5_000)
    expect(api.continueFlow).toHaveBeenCalledTimes(2)
  })

  it('cancels an unfinished flow on the server', async () => {
    const flow = create(step('choose', { choices: [{ key: 'a', label: text('A'), description: null }] }))
    await flow.begin()
    await flow.cancel()
    expect(api.cancelFlow).toHaveBeenCalledWith('flow-1')
    expect(flow.step.value).toBeNull()
  })

  it('cancels when disposed half-way', async () => {
    const flow = create(step('progress', { timeoutSeconds: 30 }))
    await flow.begin()
    flow.dispose()
    await flushPromises()
    expect(api.cancelFlow).toHaveBeenCalledWith('flow-1')
    await vi.advanceTimersByTimeAsync(3_000)
    expect(api.continueFlow).not.toHaveBeenCalled()
  })

  it('reports a flow that could not start', async () => {
    const flow = create(new ApiError({ code: 'runtime.standby', status: 409 }, 409))
    await flow.begin()
    expect(flow.step.value).toBeNull()
    expect((flow.error.value as ApiError).problem?.code).toBe('runtime.standby')
  })
})
