import { client } from './generated/client.gen'

// Everything generated from openapi.json: one function per operation (named by operationId) and the DTO types.
export * from './generated'
export { client } from './generated/client.gen'

export interface ConfigureApiClientOptions {
  /** Called whenever the API answers 401 (no session, or the session ended). */
  onUnauthorized?: () => void
}

let unauthorizedInterceptor: number | undefined

/**
 * Configures the shared client for the same-origin SPA: relative URLs, the session cookie, and the `X-ControlHome: 1`
 * header the API requires on every mutating request. Call once at startup; calling again replaces `onUnauthorized`.
 */
export function configureApiClient(options: ConfigureApiClientOptions = {}): void {
  client.setConfig({
    baseUrl: '',
    credentials: 'same-origin',
    headers: { 'X-ControlHome': '1' },
  })

  if (unauthorizedInterceptor !== undefined) {
    client.interceptors.response.eject(unauthorizedInterceptor)
    unauthorizedInterceptor = undefined
  }

  const { onUnauthorized } = options
  if (onUnauthorized) {
    unauthorizedInterceptor = client.interceptors.response.use((response) => {
      if (response.status === 401) onUnauthorized()
      return response
    })
  }
}

/**
 * The stable error `code` of an API error (RFC 7807 ProblemDetails), e.g. `auth.invalid_credentials`; the UI shows
 * `t('errors.' + code)`. Returns `undefined` for anything that is not a ProblemDetails body.
 */
export function getProblemCode(error: unknown): string | undefined {
  if (typeof error === 'object' && error !== null && 'code' in error) {
    const { code } = error as { code: unknown }
    return typeof code === 'string' ? code : undefined
  }
  return undefined
}
