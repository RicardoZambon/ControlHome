import { describe, expect, it } from 'vitest'
import { safeRedirect } from './redirect'

describe('safeRedirect', () => {
  it.each(['/', '/users', '/users?tab=1#top', '/dev/ui-kit'])('accepts the app path %s', (path) => {
    expect(safeRedirect(path)).toBe(path)
  })

  it('takes the first value of a repeated query parameter', () => {
    expect(safeRedirect(['/profile', '/users'])).toBe('/profile')
  })

  it.each([
    'https://evil.example/',
    '//evil.example',
    '/\\evil.example',
    'javascript:alert(1)',
    'users',
    '/users\nSet-Cookie: x',
    '',
    undefined,
    null,
    42,
  ])('rejects %o', (value) => {
    expect(safeRedirect(value)).toBeUndefined()
  })
})
