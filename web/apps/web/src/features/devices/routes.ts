import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/devices',
    name: 'devices',
    component: () => import('./pages/DevicesPage.vue'),
    meta: { titleKey: 'devices.title' },
  },
  {
    path: '/devices/:id',
    name: 'device',
    component: () => import('./pages/DeviceDetailPage.vue'),
    meta: { titleKey: 'devices.detail.title' },
  },
]
