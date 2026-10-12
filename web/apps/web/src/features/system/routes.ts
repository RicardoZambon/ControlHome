import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/forbidden',
    name: 'forbidden',
    component: () => import('./pages/ForbiddenPage.vue'),
    meta: { titleKey: 'system.forbidden.title' },
  },
  {
    // Every token and primitive, for visual review (linked from the user menu under "Developer").
    path: '/dev/ui-kit',
    name: 'ui-kit',
    component: () => import('./pages/UiKitPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'system.uiKit.title' },
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('./pages/NotFoundPage.vue'),
    meta: { titleKey: 'system.notFound.title' },
  },
]
