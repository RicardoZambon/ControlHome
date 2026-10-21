import { useNow } from '@vueuse/core'
import { computed, ref, type ComputedRef } from 'vue'

/**
 * The server's clock, as this browser knows it. State timestamps, history and "last seen" times come from the server's
 * runtime clock, which every API response states (`X-ControlHome-Time`); "now" for relative times ("3 minutes ago") and
 * the right edge of charts is this browser's clock plus the last offset seen, so a phone whose clock is off by a few
 * minutes (or hours) never shows "last seen in 4 hours".
 */

export const SERVER_TIME_HEADER = 'X-ControlHome-Time'

/** Server time minus browser time, in ms. */
const offset = ref(0)

/** Notes the server time an API response carried (its header value), received at `receivedAt` (browser ms). */
export function observeServerTime(header: string | null | undefined, receivedAt = Date.now()): void {
  if (!header) return
  const server = Date.parse(header)
  if (!Number.isFinite(server)) return
  offset.value = server - receivedAt
}

/** The offset in ms (server − browser). */
export function serverOffsetMs(): number {
  return offset.value
}

/** The server's time now (ms), or at a browser instant. */
export function serverNow(browserTime = Date.now()): number {
  return browserTime + offset.value
}

/** Forgets the offset (tests). */
export function resetServerClock(): void {
  offset.value = 0
}

/** The server's "now" as a reactive date, ticking every `interval` ms (and when the offset changes). */
export function useServerNow(interval = 30_000): ComputedRef<Date> {
  const now = useNow({ interval })
  return computed(() => new Date(now.value.getTime() + offset.value))
}
