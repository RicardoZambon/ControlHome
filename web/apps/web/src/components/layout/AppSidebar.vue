<script setup lang="ts">
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
  cn,
  useSidebar,
} from '@controlhome/ui'
import { computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { isNavItemActive, navItems, visibleNavGroups, type NavItem } from '../../lib/nav'
import { useDevicesStore } from '../../stores/devices'
import { useHomeStore } from '../../stores/home'
import { useSessionStore } from '../../stores/session'
import BrandMark from '../brand/BrandMark.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSessionStore()
const homeStore = useHomeStore()
const devices = useDevicesStore()
const { isMobile, setOpenMobile } = useSidebar()

const groups = computed(() => visibleNavGroups(navItems, session.me?.roles ?? []))

const isActive = (item: NavItem) => isNavItemActive(item, route, router)

// The mobile menu is a sheet: close it once a page is chosen.
watch(
  () => route.fullPath,
  () => {
    if (isMobile.value) setOpenMobile(false)
  },
)

const status = computed(() => session.status)
const databaseReady = computed(() => status.value?.databaseState === 'Ready')
// The informational version may carry build metadata (`0.3.0+abc123`); the short form is enough here.
const version = computed(() => status.value?.version.split('+')[0])
/** Devices that can't be reached (once the device list is in): the footer says so instead of "running". */
const offlineCount = computed(() =>
  devices.loaded
    ? devices.devices.filter((device) => !device.isOrphaned && devices.availabilityOf(device) === 'Offline')
        .length
    : 0,
)
const healthy = computed(() => databaseReady.value && offlineCount.value === 0)
const statusText = computed(() => {
  const state = status.value?.databaseState
  if (state && !databaseReady.value) {
    const known = ['NotConfigured', 'Connecting', 'Migrating', 'Ready', 'Failed']
    return t('shell.systemDatabase', { state: known.includes(state) ? t(`databaseStates.${state}`) : state })
  }
  if (offlineCount.value > 0) return t('shell.devicesOffline', { n: offlineCount.value }, offlineCount.value)
  return t('shell.systemReady')
})
</script>

<template>
  <Sidebar
    collapsible="icon"
    variant="inset"
    :mobile-title="t('shell.menuTitle')"
    :mobile-description="t('shell.menuDescription')"
  >
    <SidebarHeader class="pt-3">
      <SidebarMenu>
        <SidebarMenuItem>
          <SidebarMenuButton size="lg" as-child :tooltip="t('app.name')" class="gap-3">
            <RouterLink :to="{ name: 'dashboard' }">
              <BrandMark class="size-8 rounded-md" />
              <span class="grid min-w-0 flex-1 leading-tight">
                <span class="truncate font-serif text-base font-semibold tracking-tight">{{
                  t('app.name')
                }}</span>
                <span v-if="homeStore.home" class="truncate text-xs text-muted-foreground">
                  {{ homeStore.home.name }}
                </span>
              </span>
            </RouterLink>
          </SidebarMenuButton>
        </SidebarMenuItem>
      </SidebarMenu>
    </SidebarHeader>

    <SidebarContent>
      <nav :aria-label="t('shell.primaryNavigation')">
        <SidebarGroup v-for="group in groups" :key="group.id">
          <SidebarGroupLabel v-if="group.labelKey">{{ t(group.labelKey) }}</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              <SidebarMenuItem v-for="item in group.items" :key="item.to.name">
                <SidebarMenuButton
                  as-child
                  :is-active="isActive(item)"
                  :tooltip="t(item.labelKey)"
                  class="in-data-[mobile=true]:h-11 in-data-[mobile=true]:text-base"
                >
                  <RouterLink :to="item.to">
                    <component :is="item.icon" />
                    <span>{{ t(item.labelKey) }}</span>
                  </RouterLink>
                </SidebarMenuButton>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </nav>
    </SidebarContent>

    <SidebarFooter class="pb-3">
      <div
        class="flex items-center gap-2.5 rounded-md px-2 py-1.5 text-xs text-muted-foreground group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-0"
        role="status"
      >
        <span class="relative flex size-2.5 shrink-0" aria-hidden="true">
          <span
            v-if="healthy"
            class="absolute inset-0 rounded-full bg-state-on/50 motion-safe:animate-ping motion-safe:[animation-duration:3s]"
          />
          <span :class="cn('relative size-2.5 rounded-full', healthy ? 'bg-state-on' : 'bg-state-warn')" />
        </span>
        <span class="min-w-0 truncate group-data-[collapsible=icon]:sr-only">
          {{ statusText }}
          <span v-if="version" class="text-muted-foreground"> · v{{ version }}</span>
        </span>
      </div>
    </SidebarFooter>
    <SidebarRail :label="t('shell.toggleSidebar')" />
  </Sidebar>
</template>
