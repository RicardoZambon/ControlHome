<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSessionStore } from '../../stores/session'
import BrandLockup from '../brand/BrandLockup.vue'
import FloorPlanArt from '../brand/FloorPlanArt.vue'
import LanguageMenu from './LanguageMenu.vue'
import ThemeMenu from './ThemeMenu.vue'

/** The layout before sign-in (sign-in, setup, password reset): an illustrated panel and the form. */
const { t } = useI18n()
const session = useSessionStore()
const version = computed(() => session.status?.version.split('+')[0])
</script>

<template>
  <div class="grid min-h-svh lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]">
    <aside
      class="relative hidden overflow-hidden border-r bg-sidebar text-sidebar-foreground lg:flex lg:flex-col lg:justify-between lg:gap-10 lg:p-12 xl:p-16"
    >
      <!-- Atmosphere: a soft wash of the brand color from the top corner. -->
      <div
        class="pointer-events-none absolute inset-0 bg-radial-[at_0%_0%] from-accent to-transparent to-70%"
      />
      <BrandLockup class="relative" />
      <div class="relative mx-auto w-full max-w-lg">
        <FloorPlanArt surface-class="fill-sidebar" />
      </div>
      <div class="relative max-w-md space-y-3">
        <p class="font-serif text-3xl leading-tight font-medium tracking-tight text-balance xl:text-4xl">
          {{ t('app.tagline') }}
        </p>
        <p class="text-pretty text-muted-foreground">{{ t('app.taglineDetail') }}</p>
      </div>
    </aside>

    <div class="relative flex min-h-svh min-w-0 flex-col">
      <div
        class="pointer-events-none absolute inset-x-0 top-0 h-72 bg-radial-[at_50%_0%] from-accent to-transparent to-70% lg:hidden"
      />
      <!-- On wide screens: the same top offset as the brand in the side panel, so both rows line up. -->
      <header
        class="relative flex items-center justify-between gap-3 px-4 py-4 sm:px-8 sm:py-6 lg:px-12 lg:pt-12 lg:pb-0 xl:px-16 xl:pt-16"
      >
        <BrandLockup class="lg:invisible" />
        <div class="flex items-center gap-1">
          <ThemeMenu />
          <LanguageMenu />
        </div>
      </header>
      <main
        id="main-content"
        tabindex="-1"
        class="relative flex flex-1 justify-center px-4 pt-4 pb-12 outline-none sm:px-8 sm:pt-8 lg:items-center lg:pt-0"
      >
        <slot />
      </main>
      <footer
        v-if="version"
        class="relative px-4 pb-6 text-center text-xs text-muted-foreground sm:px-8 lg:pb-12 xl:pb-16"
      >
        {{ t('app.name') }} · {{ t('app.version', { version }) }}
      </footer>
    </div>
  </div>
</template>
