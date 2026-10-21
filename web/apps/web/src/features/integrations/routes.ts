import type { RouteRecordRaw } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  {
    path: '/integrations',
    name: 'integrations',
    component: () => import('./pages/IntegrationsPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.title' },
  },
  {
    path: '/integrations/add',
    name: 'integration-catalog',
    component: () => import('./pages/IntegrationCatalogPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.catalog.title' },
  },
  {
    path: '/integrations/add/:connector',
    name: 'integration-setup',
    component: () => import('./pages/IntegrationSetupPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.setup.title' },
  },
  {
    path: '/integrations/:id',
    name: 'integration',
    component: () => import('./pages/IntegrationDetailPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.detail.title' },
  },
  {
    path: '/integrations/:id/reauth',
    name: 'integration-reauth',
    component: () => import('./pages/IntegrationSetupPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.setup.reauthTitle' },
  },
]
