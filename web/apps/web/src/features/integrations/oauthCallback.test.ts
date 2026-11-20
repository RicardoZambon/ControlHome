import { afterEach, describe, expect, it } from 'vitest'
import { handOver, isCallbackUrl, listenForCallback } from './oauthCallback'

const callback = 'https://controlhome.example.com/oauth/callback?code=abc&state=xyz'

describe('oauth callback hand-off', () => {
  let stop = () => {}
  afterEach(() => stop())

  it('accepts only a sign-in answer with a state', () => {
    expect(isCallbackUrl(callback)).toBe(true)
    expect(
      isCallbackUrl('https://controlhome.example.com/oauth/callback?error=access_denied&state=xyz'),
    ).toBe(true)
    expect(isCallbackUrl('https://controlhome.example.com/oauth/callback?code=abc')).toBe(false)
    expect(isCallbackUrl('not a url')).toBe(false)
  })

  it('hands the address to a listening wizard, which confirms it', async () => {
    const taken: string[] = []
    stop = listenForCallback((url) => {
      taken.push(url)
      return true
    })

    await expect(handOver(callback)).resolves.toBe(true)
    expect(taken).toEqual([callback])
  })

  it('reports when no wizard takes the address', async () => {
    stop = listenForCallback(() => false)
    await expect(handOver(callback, 200)).resolves.toBe(false)
  })
})
