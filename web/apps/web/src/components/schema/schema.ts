import type { FieldSchemaDto, LocalizedTextDto, ValueSchema } from '@controlhome/api-client'

/**
 * Schema-driven values (docs/architecture.md §3 "ValueSchema types"): defaults, bounds, per-endpoint constraints and a
 * validator that mirrors the server's `ValueSchemaValidator`, so forms catch most mistakes before a request.
 */

/** Narrowing of one value (an endpoint's `constraints.attributes[key]`, with the `dependsOn` case already applied). */
export interface ValueConstraint {
  min?: number | null
  max?: number | null
  step?: number | null
  values?: readonly string[] | null
}

/** One field of a `<SchemaForm>`. */
export interface SchemaFieldDefinition {
  key: string
  label: string
  schema: ValueSchema
  required?: boolean
  /** Write-only (stored encrypted): rendered as a password field. */
  secret?: boolean
  help?: string
  constraint?: ValueConstraint
  /** Shown but not editable. */
  readonly?: boolean
  /** Rarely changed: folded away under "Advanced", after the other fields. */
  advanced?: boolean
}

/** Why a value does not fit (the server's `ValueErrorKind`, snake_case like its error codes). */
export type SchemaErrorKind =
  'required' | 'invalid_type' | 'out_of_range' | 'not_on_step' | 'not_allowed' | 'too_long'

const EPSILON = 1e-9

export interface NumberBounds {
  min?: number
  max?: number
  /** The step grid, from `origin`. */
  step?: number
  origin: number
  integer: boolean
  unit?: string
}

function larger(a: number | null | undefined, b: number | null | undefined): number | undefined {
  if (a === null || a === undefined) return b ?? undefined
  if (b === null || b === undefined) return a
  return Math.max(a, b)
}

function smaller(a: number | null | undefined, b: number | null | undefined): number | undefined {
  if (a === null || a === undefined) return b ?? undefined
  if (b === null || b === undefined) return a
  return Math.min(a, b)
}

/** The effective range and step of a number schema narrowed by a constraint (the constraint's step wins). */
export function numberBounds(
  schema: Extract<ValueSchema, { type: 'number' }>,
  constraint?: ValueConstraint,
): NumberBounds {
  const min = larger(schema.min, constraint?.min)
  const max = smaller(schema.max, constraint?.max)
  const constrainedStep = constraint?.step ?? undefined
  const step = constrainedStep ?? schema.step ?? undefined
  const origin = constrainedStep !== undefined ? (constraint?.min ?? schema.min ?? 0) : (schema.min ?? 0)
  return {
    min,
    max,
    step: step !== undefined && step > 0 ? step : undefined,
    origin,
    integer: schema.integer ?? false,
    unit: schema.unit ?? undefined,
  }
}

/** The values an enum (or a constrained string) may take, in display order. */
export function allowedValues(schema: ValueSchema, constraint?: ValueConstraint): string[] {
  const base =
    schema.type === 'enum'
      ? schema.values
      : schema.type === 'momentary'
        ? (schema.events ?? [])
        : schema.type === 'flags'
          ? schema.flags
          : (constraint?.values ?? [])
  const allowed = constraint?.values
  return allowed && schema.type !== 'flags' ? base.filter((value) => allowed.includes(value)) : [...base]
}

/** Snaps a number onto the bounds' range and step grid. */
export function snapNumber(value: number, bounds: NumberBounds): number {
  let result = value
  if (bounds.step) {
    const steps = Math.round((result - bounds.origin) / bounds.step)
    result = bounds.origin + steps * bounds.step
    // Floating-point noise from the multiplication (0.1 * 3).
    const decimals = Math.min(10, (String(bounds.step).split('.')[1] ?? '').length)
    result = Number(result.toFixed(decimals))
  }
  if (bounds.integer) result = Math.round(result)
  if (bounds.min !== undefined) result = Math.max(bounds.min, result)
  if (bounds.max !== undefined) result = Math.min(bounds.max, result)
  return result
}

/** A sensible starting value for a new form field. */
export function defaultValueFor(schema: ValueSchema, constraint?: ValueConstraint): unknown {
  switch (schema.type) {
    case 'bool':
      return false
    case 'number': {
      const bounds = numberBounds(schema, constraint)
      return bounds.min ?? (bounds.max !== undefined && bounds.max < 0 ? bounds.max : 0)
    }
    case 'enum':
      return allowedValues(schema, constraint)[0] ?? ''
    case 'string':
      return ''
    case 'flags':
      return []
    default:
      return undefined
  }
}

function isEmpty(value: unknown): boolean {
  return value === null || value === undefined || value === ''
}

/** Checks one value like the server does; `null` when it is valid. Empty optional values are valid. */
export function validateValue(
  schema: ValueSchema,
  value: unknown,
  options: { required?: boolean; constraint?: ValueConstraint } = {},
): SchemaErrorKind | null {
  if (isEmpty(value)) return options.required ? 'required' : null
  const constraint = options.constraint
  switch (schema.type) {
    case 'bool':
      return typeof value === 'boolean' ? null : 'invalid_type'
    case 'number': {
      if (typeof value !== 'number' || !Number.isFinite(value)) return 'invalid_type'
      const bounds = numberBounds(schema, constraint)
      if (bounds.integer && Math.abs(value - Math.round(value)) > EPSILON) return 'invalid_type'
      if (
        (bounds.min !== undefined && value < bounds.min - EPSILON) ||
        (bounds.max !== undefined && value > bounds.max + EPSILON)
      )
        return 'out_of_range'
      if (bounds.step) {
        const steps = (value - bounds.origin) / bounds.step
        if (Math.abs(steps - Math.round(steps)) > 1e-6) return 'not_on_step'
      }
      return null
    }
    case 'enum':
      if (typeof value !== 'string') return 'invalid_type'
      return allowedValues(schema, constraint).includes(value) ? null : 'not_allowed'
    case 'string':
      if (typeof value !== 'string') return 'invalid_type'
      if (schema.maxLength !== null && schema.maxLength !== undefined && value.length > schema.maxLength)
        return 'too_long'
      return constraint?.values && !constraint.values.includes(value) ? 'not_allowed' : null
    case 'flags': {
      if (!Array.isArray(value)) return 'invalid_type'
      const seen = new Set<string>()
      for (const flag of value) {
        if (typeof flag !== 'string') return 'invalid_type'
        if (!schema.flags.includes(flag) || seen.has(flag)) return 'not_allowed'
        seen.add(flag)
      }
      return null
    }
    case 'momentary': {
      if (typeof value !== 'string') return 'invalid_type'
      const events = schema.events ?? []
      return events.length === 0 || events.includes(value) ? null : 'not_allowed'
    }
    default:
      return null
  }
}

/** Validates every field of a form; returns the errors by field key (empty when the form is valid). */
export function validateFields(
  fields: readonly SchemaFieldDefinition[],
  values: Record<string, unknown>,
): Record<string, SchemaErrorKind> {
  const errors: Record<string, SchemaErrorKind> = {}
  for (const field of fields) {
    if (field.readonly) continue
    const error = validateValue(field.schema, values[field.key], {
      required: field.required,
      constraint: field.constraint,
    })
    if (error) errors[field.key] = error
  }
  return errors
}

interface RawConstraint {
  min?: number | null
  max?: number | null
  step?: number | null
  values?: string[] | null
  dependsOn?: string | null
  cases?: Record<string, RawConstraint> | null
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * The constraint of attribute `key` from an endpoint capability's `constraints` JSON
 * (`{"attributes": {"<key>": {min, max, step, values, dependsOn, cases}}}`), with the case selected by the current value
 * of its `dependsOn` attribute applied on top (both must hold, like on the server). `undefined` when there is none.
 */
export function constraintFor(
  constraints: unknown,
  key: string,
  current?: (attributeKey: string) => unknown,
): ValueConstraint | undefined {
  if (!isObject(constraints) || !isObject(constraints.attributes)) return undefined
  const raw = constraints.attributes[key]
  if (!isObject(raw)) return undefined
  const base = raw as RawConstraint
  let result: ValueConstraint = {
    min: base.min ?? undefined,
    max: base.max ?? undefined,
    step: base.step ?? undefined,
    values: base.values ?? undefined,
  }
  if (base.dependsOn && isObject(base.cases) && current) {
    const selector = current(base.dependsOn)
    const selected = typeof selector === 'string' ? base.cases[selector] : undefined
    if (isObject(selected)) {
      const extra = selected as RawConstraint
      result = {
        min: larger(result.min, extra.min),
        max: smaller(result.max, extra.max),
        step: extra.step ?? result.step,
        values:
          result.values && extra.values
            ? result.values.filter((value) => extra.values!.includes(value))
            : (extra.values ?? result.values),
      }
    }
  }
  return result
}

/** A text in the UI language. */
export function localized(text: LocalizedTextDto | null | undefined, locale: string): string {
  if (!text) return ''
  return locale.toLowerCase().startsWith('pt') ? text.ptBr || text.en : text.en || text.ptBr
}

/** The fields of a connector's configuration form (`ConnectorDto.configFields`). */
export function fieldsFromFieldSchemas(
  fields: readonly FieldSchemaDto[],
  locale: string,
): SchemaFieldDefinition[] {
  return fields.map((field) => ({
    key: field.key,
    label: localized(field.label, locale),
    schema: field.schema,
    required: field.required,
    secret: field.secret,
    advanced: field.advanced,
    help: field.help ? localized(field.help, locale) : undefined,
  }))
}

/** "fanMode" / "remote_control" → "Fan mode" / "Remote control": a readable fallback for untranslated keys. */
export function humanize(key: string): string {
  const words = key
    .replace(/[_.:-]+/g, ' ')
    .replace(/([a-z\d])([A-Z])/g, '$1 $2')
    .trim()
    .toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}
