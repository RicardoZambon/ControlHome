import 'vue-router'

export type AppLayout = 'shell' | 'bare'

declare module 'vue-router' {
  interface RouteMeta {
    /** Reachable without a session (sign-in, setup, password reset). */
    public?: boolean
    /** Public page that a signed-in user skips (sign-in). */
    guestOnly?: boolean
    /** Only users with one of these roles may open the page (others go to /forbidden). */
    roles?: readonly string[]
    /** `shell` (sidebar + top bar, default) or `bare` (centered, for sign-in and setup). */
    layout?: AppLayout
    /** i18n key of the page title (document title and top bar). */
    titleKey?: string
  }
}
