import type { StateValue } from '@controlhome/api-client'

/**
 * Attribute values as they travel (`StateValue` = `{ kind, b?, n?, s?, j? }`, see docs/architecture.md §3). The server
 * leaves out the members it does not set, so every reader here accepts a partial value.
 */

export const NULL_STATE: StateValue = Object.freeze({ kind: 'null' })

export function boolState(value: boolean): StateValue {
  return { kind: 'bool', b: value }
}

export function numberState(value: number): StateValue {
  return { kind: 'number', n: value }
}

export function stringState(value: string): StateValue {
  return { kind: 'string', s: value }
}

export function jsonState(value: unknown): StateValue {
  return { kind: 'json', j: value }
}

/** A plain JS value (a command argument, a form value) as a StateValue. */
export function toStateValue(value: unknown): StateValue {
  if (value === null || value === undefined) return NULL_STATE
  if (typeof value === 'boolean') return boolState(value)
  if (typeof value === 'number') return Number.isFinite(value) ? numberState(value) : NULL_STATE
  if (typeof value === 'string') return stringState(value)
  return jsonState(value)
}

/** The plain JS value of a StateValue (`undefined` for none or a null value). */
export function fromStateValue(value: Partial<StateValue> | null | undefined): unknown {
  switch (value?.kind) {
    case 'bool':
      return value.b ?? undefined
    case 'number':
      return value.n ?? undefined
    case 'string':
      return value.s ?? undefined
    case 'json':
      return value.j ?? undefined
    default:
      return undefined
  }
}

export function asBool(value: Partial<StateValue> | null | undefined): boolean | undefined {
  return value?.kind === 'bool' && typeof value.b === 'boolean' ? value.b : undefined
}

export function asNumber(value: Partial<StateValue> | null | undefined): number | undefined {
  return value?.kind === 'number' && typeof value.n === 'number' && Number.isFinite(value.n)
    ? value.n
    : undefined
}

export function asString(value: Partial<StateValue> | null | undefined): string | undefined {
  return value?.kind === 'string' && typeof value.s === 'string' ? value.s : undefined
}

/** Whether the attribute has a value at all (not missing, not `{kind:'null'}`). */
export function hasValue(value: Partial<StateValue> | null | undefined): boolean {
  return fromStateValue(value) !== undefined
}

function canonical(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonical)
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, canonical((value as Record<string, unknown>)[key])]),
    )
  }
  return value
}

/** Structural equality, ignoring JSON member order (as the server compares them). */
export function sameState(
  a: Partial<StateValue> | null | undefined,
  b: Partial<StateValue> | null | undefined,
): boolean {
  const kindA = a?.kind ?? 'null'
  const kindB = b?.kind ?? 'null'
  if (kindA !== kindB) return false
  return JSON.stringify(canonical(fromStateValue(a))) === JSON.stringify(canonical(fromStateValue(b)))
}
