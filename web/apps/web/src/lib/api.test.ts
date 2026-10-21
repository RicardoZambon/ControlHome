import { client, getHome, getMe, listUsers, login } from '@controlhome/api-client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse } from '../test/app'
import { isSessionProbe, setupApi } from './api'
import { resetServerClock, serverOffsetMs } from './serverClock'

function useFakeServer(status: number, body: unknown = { code: 'auth.required' }) {
  const fetch = vi.fn(async (request: Request) => {
    void request
    return jsonResponse(status, body, 'application/problem+json')
  })
  client.setConfig({ baseUrl: 'http://localhost', fetch: fetch as unknown as typeof globalThis.fetch })
  return fetch
}

afterEach(() => {
  client.interceptors.response.clear()
})

describe('isSessionProbe', () => {
  it.each([
    ['GET', '/api/auth/me', true],
    ['POST', '/api/auth/login', true],
    ['PUT', '/api/auth/me/profile', false],
    ['GET', '/api/users', false],
    ['GET', 'http://localhost:5173/api/auth/me?x=1', true],
  ])('%s %s → %s', (method, url, expected) => {
    expect(isSessionProbe({ method, url })).toBe(expected)
  })
})

describe('setupApi', () => {
  it('reports an ended session on a 401 from any other request', async () => {
    const onSessionExpired = vi.fn()
    setupApi(onSessionExpired)
    const fetch = useFakeServer(401)

    await listUsers()
    await getHome()

    expect(onSessionExpired).toHaveBeenCalledTimes(2)
    expect(fetch.mock.calls[0]![0].headers.get('X-ControlHome')).toBe('1')
  })

  it('ignores the 401 of the session probe and of a wrong password', async () => {
    const onSessionExpired = vi.fn()
    setupApi(onSessionExpired)
    useFakeServer(401)

    await getMe()
    await login({ body: { userName: 'ana', password: 'nope', rememberMe: false } })

    expect(onSessionExpired).not.toHaveBeenCalled()
  })

  it('keeps the server clock from the time every response states', async () => {
    setupApi(vi.fn())
    const server = new Date(Date.now() + 2 * 3_600_000).toISOString()
    client.setConfig({
      baseUrl: 'http://localhost',
      fetch: (async () =>
        new Response('[]', {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'X-ControlHome-Time': server },
        })) as unknown as typeof globalThis.fetch,
    })

    await listUsers()

    expect(Math.abs(serverOffsetMs() - 2 * 3_600_000)).toBeLessThan(1_000)
    resetServerClock()
  })

  it('ignores other statuses', async () => {
    const onSessionExpired = vi.fn()
    setupApi(onSessionExpired)
    useFakeServer(403, { code: 'auth.forbidden' })

    await listUsers()

    expect(onSessionExpired).not.toHaveBeenCalled()
  })
})
