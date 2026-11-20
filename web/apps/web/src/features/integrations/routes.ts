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
    // An app created with the ControlHome address redirects here after the vendor sign-in (see oauthCallback.ts).
    // Public: it only hands the address to the wizard open in another tab, which checks it.
    path: '/oauth/callback',
    name: 'oauth-callback',
    component: () => import('./pages/OAuthCallbackPage.vue'),
    meta: { public: true, layout: 'bare', titleKey: 'integrations.oauthCallback.title' },
  },
  {
    path: '/integrations/:id/reauth',
    name: 'integration-reauth',
    component: () => import('./pages/IntegrationSetupPage.vue'),
    meta: { roles: ['Admin'], titleKey: 'integrations.setup.reauthTitle' },
  },
]
