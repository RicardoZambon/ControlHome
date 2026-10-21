import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/settings/backup',
    name: 'settings-backup',
    component: () => import('./pages/BackupPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'settings.backup.title' },
  },
  {
    path: '/settings/history',
    name: 'settings-history',
    component: () => import('./pages/HistorySettingsPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'settings.history.title' },
  },
]
