<script setup lang="ts">
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
  Separator,
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
} from '@controlhome/ui'
import { Menu, PanelLeft } from '@lucide/vue'
import { useMediaQuery } from '@vueuse/core'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { pageTrail, type TrailItem } from '../../composables/usePageTrail'
import { useHomeStore } from '../../stores/home'
import BrandMark from '../brand/BrandMark.vue'
import AppSidebar from './AppSidebar.vue'
import AppTabBar from './AppTabBar.vue'
import LanguageMenu from './LanguageMenu.vue'
import ThemeMenu from './ThemeMenu.vue'
import UserMenu from './UserMenu.vue'

/**
 * The signed-in layout: collapsible sidebar, top bar and the page. On phones the sidebar is a sheet opened from the
 * menu button, and the main pages are also a bottom tab bar (except on focused tasks, such as the floor-plan editor).
 * The top bar's breadcrumb is the home and the page: the route's title, or the trail the page set (`usePageTrail`);
 * phones show only its last step.
 */
const { t } = useI18n()
const route = useRoute()
const homeStore = useHomeStore()
// The same breakpoint as SidebarProvider's mobile sheet.
const isPhone = useMediaQuery('(max-width: 768px)')

const pageTitle = computed(() => (route.meta.titleKey ? t(route.meta.titleKey) : t('app.name')))
const trail = computed<readonly TrailItem[]>(() => pageTrail.value ?? [{ label: pageTitle.value }])

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
        class="sticky top-0 z-20 flex h-14 shrink-0 items-center gap-1 border-b bg-background/85 px-2 backdrop-blur-md sm:gap-2 md:rounded-t-xl md:px-5"
      >
        <SidebarTrigger
          :label="isPhone ? t('shell.openNavigation') : t('shell.toggleSidebar')"
          class="md:-ml-1"
        >
          <Menu v-if="isPhone" />
          <PanelLeft v-else />
        </SidebarTrigger>
        <RouterLink
          :to="{ name: 'dashboard' }"
          class="flex shrink-0 items-center rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background md:hidden"
          :aria-label="homeStore.home?.name ?? t('app.name')"
        >
          <BrandMark class="size-7 rounded-md" />
        </RouterLink>
        <Separator orientation="vertical" class="mr-1 hidden data-[orientation=vertical]:h-5 md:block" />
        <Breadcrumb :label="t('shell.breadcrumb')" class="ml-1 min-w-0 md:ml-0">
          <BreadcrumbList class="flex-nowrap">
            <template v-if="homeStore.home">
              <BreadcrumbItem class="hidden min-w-0 sm:inline-flex">
                <span class="truncate">{{ homeStore.home.name }}</span>
              </BreadcrumbItem>
              <BreadcrumbSeparator class="hidden sm:block" />
            </template>
            <template v-for="(item, index) in trail" :key="index">
              <template v-if="index < trail.length - 1">
                <BreadcrumbItem class="hidden min-w-0 sm:inline-flex">
                  <BreadcrumbLink v-if="item.to" as-child>
                    <RouterLink :to="item.to" class="truncate">{{ item.label }}</RouterLink>
                  </BreadcrumbLink>
                  <span v-else class="truncate">{{ item.label }}</span>
                </BreadcrumbItem>
                <BreadcrumbSeparator class="hidden sm:block" />
              </template>
              <BreadcrumbItem v-else class="min-w-0">
                <BreadcrumbPage class="truncate font-medium">{{ item.label }}</BreadcrumbPage>
              </BreadcrumbItem>
            </template>
          </BreadcrumbList>
        </Breadcrumb>
        <div class="ml-auto flex shrink-0 items-center sm:gap-1">
          <ThemeMenu />
          <LanguageMenu />
          <Separator orientation="vertical" class="mx-1 hidden data-[orientation=vertical]:h-5 sm:block" />
          <UserMenu />
        </div>
      </header>
      <div
        id="main-content"
        tabindex="-1"
        :class="[
          'flex-1 outline-none',
          route.meta.fullWidth
            ? 'px-3 pt-3 sm:px-4 md:px-5 md:pt-4 md:pb-5'
            : 'px-4 pt-6 pb-[calc(6rem+env(safe-area-inset-bottom))] sm:px-6 md:px-10 md:pt-10 md:pb-16',
          route.meta.fullWidth &&
            (route.meta.focused
              ? 'pb-[calc(0.75rem+env(safe-area-inset-bottom))]'
              : 'pb-[calc(4.25rem+env(safe-area-inset-bottom))]'),
        ]"
      >
        <div :class="route.meta.fullWidth ? 'w-full' : 'mx-auto w-full max-w-6xl'">
          <slot />
        </div>
      </div>
    </SidebarInset>
    <AppTabBar v-if="!route.meta.focused" />
  </SidebarProvider>
</template>
