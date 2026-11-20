/**
 * The OAuth callback hand-off. An app created with the ControlHome address sends the browser back to `/oauth/callback`
 * in a new tab. That page passes the whole address to the setup wizard still open in the first tab, which answers its
 * sign-in step with it (the flow checks the state, as for a pasted address). Same-origin tabs only.
 */
export const OAUTH_CHANNEL = 'controlhome-oauth'

export type OAuthMessage = { type: 'callback'; url: string } | { type: 'received' }

/** Whether this is a sign-in answer worth handing over (a code or an error, and a state). */
export function isCallbackUrl(url: string): boolean {
  try {
    const params = new URL(url).searchParams
    return (params.has('code') || params.has('error')) && params.has('state')
  } catch {
    return false
  }
}

function openChannel(): BroadcastChannel | null {
  return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(OAUTH_CHANNEL)
}

/**
 * Sends the callback address to the open wizard. Resolves `true` when a wizard took it within `timeoutMs`, `false`
 * otherwise (no wizard open, another origin, or no BroadcastChannel).
 */
export function handOver(url: string, timeoutMs = 2000): Promise<boolean> {
  const channel = openChannel()
  if (!channel) return Promise.resolve(false)
  return new Promise((resolve) => {
    const finish = (taken: boolean) => {
      clearTimeout(timer)
      channel.close()
      resolve(taken)
    }
    const timer = setTimeout(() => finish(false), timeoutMs)
    channel.onmessage = (event: MessageEvent<OAuthMessage>) => {
      if (event.data?.type === 'received') finish(true)
    }
    channel.postMessage({ type: 'callback', url } satisfies OAuthMessage)
  })
}

/**
 * Listens for callback addresses while a sign-in step waits for one. `take` returns whether it used the address (then
 * the callback page is told so). Returns the function that stops listening.
 */
export function listenForCallback(take: (url: string) => boolean): () => void {
  const channel = openChannel()
  if (!channel) return () => {}
  channel.onmessage = (event: MessageEvent<OAuthMessage>) => {
    const data = event.data
    if (data?.type !== 'callback' || typeof data.url !== 'string' || !isCallbackUrl(data.url)) return
    if (take(data.url)) channel.postMessage({ type: 'received' } satisfies OAuthMessage)
  }
  return () => channel.close()
}
