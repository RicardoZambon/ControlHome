import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('./pages/LoginPage.vue'),
    meta: { public: true, guestOnly: true, layout: 'bare', titleKey: 'auth.login.title' },
  },
  {
    // Opened from the one-time link printed by `reset-admin <user>`: /reset-password?user=…&token=…
    path: '/reset-password',
    name: 'reset-password',
    component: () => import('./pages/ResetPasswordPage.vue'),
    meta: { public: true, layout: 'bare', titleKey: 'auth.reset.title' },
  },
]
