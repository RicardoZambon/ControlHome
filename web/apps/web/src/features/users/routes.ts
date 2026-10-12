import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/users',
    name: 'users',
    component: () => import('./pages/UsersPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'users.title' },
  },
]
