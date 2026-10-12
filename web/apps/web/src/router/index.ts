import type { Pinia } from 'pinia'
import { createRouter, createWebHistory, type RouterHistory, type RouteRecordRaw } from 'vue-router'
import { useSessionStore } from '../stores/session'
import { resolveNavigation } from './guard'
import './meta'

// Each feature exports `routes: RouteRecordRaw[]` from `features/<feature>/routes.ts`.
const routeModules = import.meta.glob<RouteRecordRaw[]>('../features/*/routes.ts', {
  eager: true,
  import: 'routes',
})

/** Every feature's routes; the catch-all (not found) goes last. */
export function collectRoutes(modules: Record<string, RouteRecordRaw[]> = routeModules): RouteRecordRaw[] {
  const routes = Object.values(modules).flat()
  const isCatchAll = (route: RouteRecordRaw) => route.path.startsWith('/:pathMatch')
  return [...routes.filter((route) => !isCatchAll(route)), ...routes.filter(isCatchAll)]
}

export function createAppRouter(options: { pinia?: Pinia; history?: RouterHistory } = {}) {
  const router = createRouter({
    history: options.history ?? createWebHistory(),
    routes: collectRoutes(),
    scrollBehavior: (_to, _from, saved) => saved ?? { top: 0 },
  })

  router.beforeEach(async (to) => {
    const session = useSessionStore(options.pinia)
    await session.ensureStatus()
    // While setup is incomplete there is no account to probe (and the database may not exist yet).
    if (session.status?.setupCompleted !== false) await session.ensureMe()
    return resolveNavigation(session.status, session.me, to)
  })

  return router
}
