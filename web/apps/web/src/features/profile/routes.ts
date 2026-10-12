import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/profile',
    name: 'profile',
    component: () => import('./pages/ProfilePage.vue'),
    meta: { titleKey: 'profile.title' },
  },
]
