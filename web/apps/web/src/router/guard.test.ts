import { describe, expect, it } from 'vitest'
import { resolveNavigation, type NavigationTarget } from './guard'

const done = { setupCompleted: true }
const pending = { setupCompleted: false }
const admin = { roles: ['Admin'] }
const member = { roles: ['Member'] }

function to(
  name: string,
  fullPath: string,
  meta: NavigationTarget['meta'] = {},
  query = {},
): NavigationTarget {
  return { name, fullPath, meta, query }
}

const login = to('login', '/login', { public: true, guestOnly: true })
const setup = to('setup', '/setup', { public: true })
const dashboard = to('dashboard', '/')
const users = to('users', '/users?tab=all', { roles: ['Admin'] })
const reset = to('reset-password', '/reset-password?user=a&token=b', { public: true })

describe('resolveNavigation', () => {
  it('sends everything to /setup until setup is completed', () => {
    expect(resolveNavigation(pending, null, dashboard)).toEqual({ name: 'setup' })
    expect(resolveNavigation(pending, null, login)).toEqual({ name: 'setup' })
    expect(resolveNavigation(pending, null, setup)).toBe(true)
  })

  it('leaves /setup once setup is completed', () => {
    expect(resolveNavigation(done, null, setup)).toEqual({ name: 'dashboard' })
    expect(resolveNavigation(done, admin, setup)).toEqual({ name: 'dashboard' })
  })

  it('asks for a session on private pages, remembering where to go back', () => {
    expect(resolveNavigation(done, null, users)).toEqual({
      name: 'login',
      query: { redirect: '/users?tab=all' },
    })
    expect(resolveNavigation(done, null, dashboard)).toEqual({ name: 'login' })
  })

  it('opens public pages without a session', () => {
    expect(resolveNavigation(done, null, login)).toBe(true)
    expect(resolveNavigation(done, null, reset)).toBe(true)
  })

  it('skips sign-in when already signed in, honouring a safe redirect', () => {
    expect(resolveNavigation(done, member, login)).toEqual({ name: 'dashboard' })
    const withRedirect = to('login', '/login?redirect=/profile', login.meta, { redirect: '/profile' })
    expect(resolveNavigation(done, member, withRedirect)).toBe('/profile')
    const offsite = to('login', '/login', login.meta, { redirect: '//evil.example' })
    expect(resolveNavigation(done, member, offsite)).toEqual({ name: 'dashboard' })
  })

  it('keeps public pages that are not guest-only open for signed-in users', () => {
    expect(resolveNavigation(done, member, reset)).toBe(true)
  })

  it('checks roles', () => {
    expect(resolveNavigation(done, admin, users)).toBe(true)
    expect(resolveNavigation(done, member, users)).toEqual({ name: 'forbidden' })
    expect(resolveNavigation(done, member, dashboard)).toBe(true)
  })

  it('skips the setup checks when the server state is unknown', () => {
    expect(resolveNavigation(null, null, setup)).toBe(true)
    expect(resolveNavigation(null, null, dashboard)).toEqual({ name: 'login' })
    expect(resolveNavigation(null, admin, dashboard)).toBe(true)
  })
})
