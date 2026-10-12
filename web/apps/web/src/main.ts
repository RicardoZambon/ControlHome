import '@fontsource-variable/onest'
import '@fontsource-variable/fraunces/full.css'
import { toast, useTheme } from '@controlhome/ui'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { createAppI18n } from './i18n'
import { setupApi } from './lib/api'
import { useThemedFavicon } from './lib/favicon'
import { createAppRouter } from './router'
import { useHomeStore } from './stores/home'
import { useSessionStore } from './stores/session'
import './styles/main.css'

// Takes over from public/theme-init.js (same storage key) before anything renders.
useTheme()
useThemedFavicon()

const i18n = createAppI18n()
const pinia = createPinia()
const router = createAppRouter({ pinia })

// A 401 on any request other than the session probe or the sign-in itself: the session ended (signed out elsewhere,
// disabled, expired). Forget the user and ask to sign in again, coming back to the same page afterwards.
setupApi(() => {
  const session = useSessionStore(pinia)
  if (!session.isAuthenticated) return
  session.clear()
  useHomeStore(pinia).reset()
  toast.info(i18n.global.t('shell.sessionExpired'))
  const current = router.currentRoute.value
  void router.push({
    name: 'login',
    query: current.meta.public ? {} : { redirect: current.fullPath },
  })
})

createApp(App).use(pinia).use(i18n).use(router).mount('#app')
