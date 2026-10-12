import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/floors',
    name: 'floors',
    component: () => import('./pages/FloorsPage.vue'),
    meta: { titleKey: 'floors.title' },
  },
  {
    path: '/floors/:id(\\d+)',
    name: 'floor',
    component: () => import('./pages/FloorViewPage.vue'),
    meta: { titleKey: 'floors.view.title' },
  },
  {
    path: '/floors/:id(\\d+)/edit',
    name: 'floor-edit',
    component: () => import('./pages/FloorEditorPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'floors.editor.title', fullWidth: true, focused: true },
  },
]
