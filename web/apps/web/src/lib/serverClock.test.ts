import { afterEach, describe, expect, it } from 'vitest'
import { observeServerTime, resetServerClock, serverNow, serverOffsetMs } from './serverClock'

afterEach(() => resetServerClock())

describe('server clock', () => {
  it('is the browser clock until the server says otherwise', () => {
    expect(serverOffsetMs()).toBe(0)
    expect(Math.abs(serverNow() - Date.now())).toBeLessThan(50)
  })

  it('keeps the offset of the server time an API response carried', () => {
    const receivedAt = Date.parse('2026-10-12T12:00:00.000Z')
    observeServerTime('2026-10-12T13:30:00.250Z', receivedAt)

    expect(serverOffsetMs()).toBe(90 * 60_000 + 250)
    expect(serverNow(receivedAt + 1_000)).toBe(Date.parse('2026-10-12T13:30:01.250Z'))
  })

  it('a phone clock that runs ahead gets a negative offset', () => {
    observeServerTime('2026-10-12T11:56:00.000Z', Date.parse('2026-10-12T12:00:00.000Z'))

    expect(serverOffsetMs()).toBe(-4 * 60_000)
  })

  it('ignores a missing or unreadable header', () => {
    observeServerTime('2026-10-12T13:00:00.000Z', Date.parse('2026-10-12T12:00:00.000Z'))
    observeServerTime(null)
    observeServerTime('yesterday-ish')

    expect(serverOffsetMs()).toBe(3_600_000)
  })
})
