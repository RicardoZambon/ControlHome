import { client, configureApiClient } from '@controlhome/api-client'

/**
 * Requests whose 401 is an answer, not an ended session: the session probe (`GET /api/auth/me`) and the login itself
 * (wrong password).
 */
export function isSessionProbe(request: Pick<Request, 'method' | 'url'>): boolean {
  const path = new URL(request.url, 'http://localhost').pathname
  return (
    (request.method === 'GET' && path === '/api/auth/me') ||
    (request.method === 'POST' && path === '/api/auth/login')
  )
}

let responseInterceptor: number | undefined

/**
 * Configures the generated client for the same-origin API (`X-ControlHome: 1`, session cookie) and calls
 * `onSessionExpired` when any other request answers 401.
 */
export function setupApi(onSessionExpired: () => void): void {
  configureApiClient()
  if (responseInterceptor !== undefined) client.interceptors.response.eject(responseInterceptor)
  responseInterceptor = client.interceptors.response.use((response, request) => {
    if (response.status === 401 && !isSessionProbe(request)) onSessionExpired()
    return response
  })
}
