<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { isNavItemActive, navItems, visibleNavGroups } from '../../lib/nav'
import { useSessionStore } from '../../stores/session'

/**
 * Phones (below md): the main pages as a bottom tab bar, always in reach of the thumb. The full navigation stays in
 * the menu sheet (top-left button).
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSessionStore()

const items = computed(() =>
  visibleNavGroups(navItems, session.me?.roles ?? [])
    .flatMap((group) => group.items)
    .filter((item) => item.tabBar !== false),
)
</script>

<template>
  <nav
    :aria-label="t('shell.tabNavigation')"
    class="fixed inset-x-0 bottom-0 z-30 border-t bg-background/95 pb-[env(safe-area-inset-bottom)] backdrop-blur-md md:hidden"
  >
    <ul class="mx-auto grid h-14 max-w-md auto-cols-fr grid-flow-col">
      <li v-for="item in items" :key="item.to.name">
        <RouterLink v-slot="{ href, navigate }" :to="item.to" custom>
          <a
            :href="href"
            :aria-current="isNavItemActive(item, route, router) ? 'page' : undefined"
            :class="
              cn(
                'flex h-full flex-col items-center justify-center gap-1 text-xs font-medium text-muted-foreground transition-colors outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset',
                isNavItemActive(item, route, router) && 'text-primary',
              )
            "
            @click="navigate"
          >
            <span
              :class="
                cn(
                  'flex h-7 w-12 items-center justify-center rounded-full transition-colors',
                  isNavItemActive(item, route, router) && 'bg-accent',
                )
              "
              aria-hidden="true"
            >
              <component :is="item.icon" class="size-5" />
            </span>
            {{ t(item.labelKey) }}
          </a>
        </RouterLink>
      </li>
    </ul>
  </nav>
</template>
