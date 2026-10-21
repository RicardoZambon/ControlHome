import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/integrations',
    name: 'integrations',
    component: () => import('./pages/IntegrationsPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.title' },
  },
]
