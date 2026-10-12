<script setup lang="ts">
import { Toaster } from '@controlhome/ui'
import { useMediaQuery } from '@vueuse/core'
import { computed, ref, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import AppShell from './components/layout/AppShell.vue'
import AppSplash from './components/layout/AppSplash.vue'
import BareLayout from './components/layout/BareLayout.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const ready = ref(false)
void router.isReady().then(() => (ready.value = true))

const layout = computed(() => (route.meta.layout === 'bare' ? BareLayout : AppShell))

watchEffect(() => {
  const name = t('app.name')
  const key = route.meta.titleKey
  document.title = key ? `${t(key)} · ${name}` : name
})

// Phones: toasts at the top, where they cover neither a dialog's buttons nor the bottom tab bar.
const isPhone = useMediaQuery('(max-width: 768px)')
const toastPosition = computed<'top-center' | 'bottom-right'>(() =>
  isPhone.value ? 'top-center' : 'bottom-right',
)

const toastOptions = computed(() => ({
  closeButtonAriaLabel: t('common.close'),
  classes: {
    success: '[&_[data-icon]]:text-state-on',
    info: '[&_[data-icon]]:text-state-info',
    warning: '[&_[data-icon]]:text-state-warn',
    error: '[&_[data-icon]]:text-destructive',
  },
}))
</script>

<template>
  <AppSplash v-if="!ready" />
  <component :is="layout" v-else>
    <RouterView v-slot="{ Component, route: current }">
      <component
        :is="Component"
        :key="current.name"
        class="motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-bottom-1 motion-safe:duration-300"
      />
    </RouterView>
  </component>
  <Toaster
    :position="toastPosition"
    close-button
    :container-aria-label="t('shell.notifications')"
    :toast-options="toastOptions"
  />
</template>
