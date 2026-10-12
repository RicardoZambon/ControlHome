import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/',
    name: 'dashboard',
    component: () => import('./pages/DashboardPage.vue'),
    meta: { titleKey: 'dashboard.title' },
  },
]
