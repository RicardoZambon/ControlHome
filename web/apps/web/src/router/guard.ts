import type { MeDto, SetupStatusDto } from '@controlhome/api-client'
import type { RouteLocationNormalized, RouteLocationRaw } from 'vue-router'
import { safeRedirect } from '../lib/redirect'
import './meta'

export type NavigationTarget = Pick<RouteLocationNormalized, 'name' | 'fullPath' | 'meta' | 'query'>

/**
 * Where a navigation may go, as a pure function of the server state (pure, so it is unit-tested without a router):
 * - setup not completed → only `/setup`; setup completed → `/setup` goes home;
 * - public pages are open (a signed-in user skips `guestOnly` pages such as sign-in);
 * - every other page needs a session (else `/login?redirect=<path>`) and one of the page's `roles` (else `/forbidden`).
 * `status === null` (server unreachable) skips the setup checks.
 */
export function resolveNavigation(
  status: Pick<SetupStatusDto, 'setupCompleted'> | null,
  me: Pick<MeDto, 'roles'> | null,
  to: NavigationTarget,
): true | RouteLocationRaw {
  const isSetup = to.name === 'setup'
  if (status && !status.setupCompleted) return isSetup ? true : { name: 'setup' }
  if (status?.setupCompleted && isSetup) return { name: 'dashboard' }

  if (to.meta.public) {
    if (me && to.meta.guestOnly) return safeRedirect(to.query.redirect) ?? { name: 'dashboard' }
    return true
  }

  if (!me) {
    return to.fullPath === '/' ? { name: 'login' } : { name: 'login', query: { redirect: to.fullPath } }
  }

  const roles = to.meta.roles
  if (roles?.length && !roles.some((role) => me.roles.includes(role))) return { name: 'forbidden' }
  return true
}
