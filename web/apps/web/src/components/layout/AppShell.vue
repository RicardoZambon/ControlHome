<script setup lang="ts">
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
  Separator,
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
} from '@controlhome/ui'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useHomeStore } from '../../stores/home'
import AppSidebar from './AppSidebar.vue'
import LanguageMenu from './LanguageMenu.vue'
import ThemeMenu from './ThemeMenu.vue'
import UserMenu from './UserMenu.vue'

/** The signed-in layout: collapsible sidebar (a sheet on phones), top bar and the page. */
const { t } = useI18n()
const route = useRoute()
const homeStore = useHomeStore()

const pageTitle = computed(() => (route.meta.titleKey ? t(route.meta.titleKey) : t('app.name')))

onMounted(() => void homeStore.load())
</script>

<template>
  <SidebarProvider>
    <a
      href="#main-content"
      class="sr-only z-50 rounded-md bg-primary px-3 py-2 text-sm font-medium text-primary-foreground focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
    >
      {{ t('shell.skipToContent') }}
    </a>
    <AppSidebar />
    <SidebarInset class="min-w-0">
      <header
        class="sticky top-0 z-20 flex h-14 shrink-0 items-center gap-2 border-b bg-background/85 px-3 backdrop-blur-md md:rounded-t-xl md:px-5"
      >
        <SidebarTrigger :label="t('shell.toggleSidebar')" class="-ml-1 size-9" />
        <Separator orientation="vertical" class="mr-1 data-[orientation=vertical]:h-5" />
        <Breadcrumb :label="t('shell.breadcrumb')" class="min-w-0">
          <BreadcrumbList class="flex-nowrap">
            <template v-if="homeStore.home">
              <BreadcrumbItem class="hidden min-w-0 sm:inline-flex">
                <span class="truncate">{{ homeStore.home.name }}</span>
              </BreadcrumbItem>
              <BreadcrumbSeparator class="hidden sm:block" />
            </template>
            <BreadcrumbItem class="min-w-0">
              <BreadcrumbPage class="truncate font-medium">{{ pageTitle }}</BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
        <div class="ml-auto flex shrink-0 items-center gap-0.5 sm:gap-1">
          <ThemeMenu />
          <LanguageMenu />
          <Separator orientation="vertical" class="mx-1 hidden data-[orientation=vertical]:h-5 sm:block" />
          <UserMenu />
        </div>
      </header>
      <div
        id="main-content"
        tabindex="-1"
        class="flex-1 px-4 pt-6 pb-16 outline-none sm:px-6 md:px-10 md:pt-10"
      >
        <div class="mx-auto w-full max-w-6xl">
          <slot />
        </div>
      </div>
    </SidebarInset>
  </SidebarProvider>
</template>
