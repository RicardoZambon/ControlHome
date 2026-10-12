import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/setup',
    name: 'setup',
    component: () => import('./pages/SetupPage.vue'),
    meta: { public: true, layout: 'bare', titleKey: 'setup.title' },
  },
]
