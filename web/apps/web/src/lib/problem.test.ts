import { describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { ApiError, applyFieldErrors, errorCodeOf, toErrorMessage, unwrap } from './problem'

const i18n = createAppI18n({ locale: 'en' })
const t = (key: string, named?: Record<string, unknown>) => i18n.global.t(key, named ?? {})
const te = (key: string) => i18n.global.te(key)

describe('toErrorMessage', () => {
  it('translates the problem code', () => {
    expect(toErrorMessage({ status: 401, code: 'auth.invalid_credentials' }, t, te)).toBe(
      'Wrong user name or password.',
    )
  })

  it('translates an ApiError', () => {
    const error = new ApiError({ status: 409, code: 'users.last_admin' }, 409)
    expect(toErrorMessage(error, t, te)).toBe(i18n.global.t('errors.users.last_admin'))
  })

  it('falls back to errors.unknown for an unknown code', () => {
    expect(toErrorMessage({ code: 'something.new' }, t, te)).toBe(i18n.global.t('errors.unknown'))
  })

  it('falls back to errors.unknown for a body that is not a problem', () => {
    expect(toErrorMessage('Bad gateway', t, te)).toBe(i18n.global.t('errors.unknown'))
  })

  it('says the server is unreachable when fetch failed', () => {
    expect(toErrorMessage(new ApiError(new TypeError('Failed to fetch')), t, te)).toBe(
      i18n.global.t('errors.network'),
    )
  })

  it('adds a "try again in" hint from retryAfterSeconds', () => {
    const lockedOut = { code: 'auth.locked_out', retryAfterSeconds: 290 }
    expect(toErrorMessage(lockedOut, t, te)).toBe(
      `${i18n.global.t('errors.auth.locked_out')} ${i18n.global.t('errors.retryInMinutes', { minutes: 5 })}`,
    )
    const rateLimited = { code: 'request.rate_limited', retryAfterSeconds: 12 }
    expect(toErrorMessage(rateLimited, t, te)).toContain(
      i18n.global.t('errors.retryInSeconds', { seconds: 12 }),
    )
  })

  it('works in Portuguese', () => {
    const pt = createAppI18n({ locale: 'pt-BR' })
    const message = toErrorMessage(
      { code: 'users.cannot_delete_self' },
      (key, named) => pt.global.t(key, named ?? {}),
      (key) => pt.global.te(key),
    )
    expect(message).toBe('Você não pode excluir a sua própria conta.')
  })
})

describe('errorCodeOf', () => {
  it.each([
    [{ code: 'db.unavailable' }, 'db.unavailable'],
    [new ApiError({ code: 'auth.disabled' }, 403), 'auth.disabled'],
    [new TypeError('Failed to fetch'), 'network'],
    [new ApiError('<html>502</html>', 502), 'unknown'],
    [undefined, 'unknown'],
  ])('%o → %s', (error, code) => {
    expect(errorCodeOf(error)).toBe(code)
  })
})

describe('unwrap', () => {
  it('returns the data', () => {
    expect(unwrap({ data: { id: 1 }, error: undefined })).toEqual({ id: 1 })
  })

  it('throws an ApiError with the problem and the status', () => {
    const response = new Response(null, { status: 409 })
    try {
      unwrap({ data: undefined, error: { code: 'users.last_admin', status: 409 }, response })
      expect.unreachable()
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError)
      expect((error as ApiError).problem?.code).toBe('users.last_admin')
      expect((error as ApiError).status).toBe(409)
    }
  })
})

describe('applyFieldErrors', () => {
  const problem = {
    code: 'validation.failed',
    errors: { 'admin.userName': ['bad'], password: ['short'], unknownField: ['x'] },
  }

  it('maps server paths to form fields with the given message', () => {
    const setFieldError = vi.fn()
    const applied = applyFieldErrors(
      problem,
      { 'admin.userName': 'userName', password: 'password' },
      setFieldError,
      (field) => `invalid ${field}`,
    )
    expect(applied).toBe(true)
    expect(setFieldError).toHaveBeenCalledWith('userName', 'invalid userName')
    expect(setFieldError).toHaveBeenCalledWith('password', 'invalid password')
    expect(setFieldError).toHaveBeenCalledTimes(2)
  })

  it('returns false when no listed field has an error', () => {
    const setFieldError = vi.fn()
    expect(applyFieldErrors(problem, { displayName: 'displayName' }, setFieldError, () => '')).toBe(false)
    expect(applyFieldErrors({ code: 'users.last_admin' }, { a: 'a' }, setFieldError, () => '')).toBe(false)
    expect(setFieldError).not.toHaveBeenCalled()
  })
})
