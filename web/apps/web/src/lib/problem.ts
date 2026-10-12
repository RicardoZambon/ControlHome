import { getProblemCode } from '@controlhome/api-client'

/** The parts of an RFC 7807 ProblemDetails body the UI uses. */
export interface Problem {
  code: string
  status?: number | null
  detail?: string | null
  /** `validation.failed`: messages by camelCase field path (`admin.userName`). */
  errors?: Record<string, string[]>
  /** `auth.locked_out` and `request.rate_limited`. */
  retryAfterSeconds?: number
  /** `db.connection_failed`: what went wrong (`login_failed`, `certificate`, `unreachable`, `no_database_access`, `other`). */
  reason?: string
  /** `db.create_denied`: the T-SQL an administrator can run to create the database and its login. */
  script?: string
}

/** A failed API call: the ProblemDetails body (when there is one), the HTTP status and the original error. */
export class ApiError extends Error {
  readonly problem: Problem | undefined
  readonly status: number | undefined

  constructor(error: unknown, status?: number) {
    const problem = getProblemCode(error) ? (error as Problem) : undefined
    super(problem?.code ?? (error instanceof Error ? error.message : 'unknown'), { cause: error })
    this.name = 'ApiError'
    this.problem = problem
    this.status = status ?? problem?.status ?? undefined
  }
}

interface ApiResult<T> {
  data?: T
  error?: unknown
  response?: Response
}

/** The data of a generated SDK call, or throws an `ApiError` carrying the problem. */
export function unwrap<T>(result: ApiResult<T>): T {
  if (result.error !== undefined) throw new ApiError(result.error, result.response?.status)
  return result.data as T
}

/** The ProblemDetails of an error (an `ApiError` or a raw problem body), if any. */
export function problemOf(error: unknown): Problem | undefined {
  if (error instanceof ApiError) return error.problem
  return getProblemCode(error) ? (error as Problem) : undefined
}

/** `fetch` rejects with a TypeError when the server can't be reached. */
function isNetworkFailure(error: unknown): boolean {
  if (error instanceof ApiError) return isNetworkFailure(error.cause)
  return error instanceof TypeError
}

/** The error code used for the message: the problem's `code`, `network`, or `unknown`. */
export function errorCodeOf(error: unknown): string {
  const problem = problemOf(error)
  if (problem) return problem.code
  return isNetworkFailure(error) ? 'network' : 'unknown'
}

/** Field errors of a `validation.failed` problem, keyed by camelCase path. */
export function fieldErrorsOf(error: unknown): Record<string, string[]> | undefined {
  const errors = problemOf(error)?.errors
  return errors && Object.keys(errors).length > 0 ? errors : undefined
}

export type Translate = (key: string, named?: Record<string, unknown>) => string
export type HasTranslation = (key: string) => boolean

/**
 * The translated message for an API error: `errors.<code>` (falling back to `errors.unknown`), plus a "try again in"
 * hint when the problem carries `retryAfterSeconds`.
 */
export function toErrorMessage(error: unknown, t: Translate, te: HasTranslation): string {
  const code = errorCodeOf(error)
  const key = te(`errors.${code}`) ? `errors.${code}` : 'errors.unknown'
  const message = t(key)
  const retryAfter = problemOf(error)?.retryAfterSeconds
  if (typeof retryAfter !== 'number' || !Number.isFinite(retryAfter) || retryAfter <= 0) return message
  const hint =
    retryAfter < 60
      ? t('errors.retryInSeconds', { seconds: Math.ceil(retryAfter) })
      : t('errors.retryInMinutes', { minutes: Math.ceil(retryAfter / 60) })
  return `${message} ${hint}`
}

/**
 * Puts the field errors of a `validation.failed` problem on a form. `fields` maps the server's camelCase paths to form
 * field names (a path that is not listed is ignored). Messages are localized by the caller (the server's text is an
 * English fallback). Returns whether at least one field got an error.
 */
export function applyFieldErrors<TField extends string>(
  error: unknown,
  fields: Record<string, TField>,
  setFieldError: (field: TField, message: string) => void,
  messageFor: (field: TField) => string,
): boolean {
  const errors = fieldErrorsOf(error)
  if (!errors) return false
  let applied = false
  for (const path of Object.keys(errors)) {
    const field = fields[path]
    if (!field) continue
    setFieldError(field, messageFor(field))
    applied = true
  }
  return applied
}
