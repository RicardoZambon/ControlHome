import { afterEach, describe, expect, it, vi } from 'vitest'
import { client, configureApiClient, getMe, getProblemCode, logout } from './index'

function jsonResponse(status: number, body: unknown, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } })
}

/** configureApiClient() plus a fake fetch; Node's Request needs an absolute URL, the browser resolves ''. */
function configure(fetch: (request: Request) => Promise<Response>, onUnauthorized?: () => void) {
  configureApiClient({ onUnauthorized })
  client.setConfig({ baseUrl: 'http://localhost', fetch: fetch as typeof globalThis.fetch })
}

afterEach(() => {
  configureApiClient()
})

describe('configureApiClient', () => {
  it('sends the X-ControlHome header and same-origin credentials', async () => {
    const fetch = vi.fn<(request: Request) => Promise<Response>>(
      async () => new Response(null, { status: 204 }),
    )
    configure(fetch)

    await logout()

    const request = fetch.mock.calls[0]![0]
    expect(request.headers.get('X-ControlHome')).toBe('1')
    expect(request.credentials).toBe('same-origin')
    expect(new URL(request.url).pathname).toBe('/api/auth/logout')
  })

  it('calls onUnauthorized on 401 and returns the problem as the error', async () => {
    const onUnauthorized = vi.fn()
    configure(
      async () => jsonResponse(401, { status: 401, code: 'auth.required' }, 'application/problem+json'),
      onUnauthorized,
    )

    const result = await getMe()

    expect(onUnauthorized).toHaveBeenCalledTimes(1)
    expect(getProblemCode(result.error)).toBe('auth.required')
  })

  it('does not call onUnauthorized for other statuses', async () => {
    const onUnauthorized = vi.fn()
    configure(async () => jsonResponse(200, { id: '1', userName: 'admin' }), onUnauthorized)

    await getMe()

    expect(onUnauthorized).not.toHaveBeenCalled()
  })

  it('replaces the previous onUnauthorized when configured again', async () => {
    const first = vi.fn()
    const second = vi.fn()
    const fetch = async () => jsonResponse(401, { code: 'auth.required' }, 'application/problem+json')
    configure(fetch, first)
    configure(fetch, second)

    await getMe()

    expect(first).not.toHaveBeenCalled()
    expect(second).toHaveBeenCalledTimes(1)
  })
})

describe('getProblemCode', () => {
  it('reads the code of a ProblemDetails body', () => {
    expect(getProblemCode({ status: 409, code: 'users.last_admin' })).toBe('users.last_admin')
  })

  it.each([undefined, null, 'oops', 42, {}, { code: 7 }])('returns undefined for %o', (value) => {
    expect(getProblemCode(value)).toBeUndefined()
  })
})
