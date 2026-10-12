<script setup lang="ts">
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  toast,
} from '@controlhome/ui'
import { ChevronsUpDown, LogOut, Palette, UserRound } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useHomeStore } from '../../stores/home'
import { useSessionStore } from '../../stores/session'
import UserAvatar from '../UserAvatar.vue'

const { t } = useI18n()
const router = useRouter()
const session = useSessionStore()
const homeStore = useHomeStore()

const me = computed(() => session.me)
const roleLabel = computed(() => {
  const role = me.value?.roles[0]
  return role === 'Admin' || role === 'Member' || role === 'Viewer' ? t(`roles.${role}`) : ''
})

async function signOut(): Promise<void> {
  await session.logout()
  homeStore.reset()
  toast(t('shell.loggedOut'))
  await router.push({ name: 'login' })
}
</script>

<template>
  <DropdownMenu v-if="me">
    <DropdownMenuTrigger as-child>
      <Button
        variant="ghost"
        class="h-control gap-2 rounded-full px-1.5 has-[>svg]:px-1.5 lg:rounded-md lg:pr-2"
        :aria-label="t('shell.userMenu', { name: me.displayName })"
      >
        <UserAvatar :name="me.displayName" :seed="me.userName" class="size-8" />
        <span class="hidden max-w-36 min-w-0 text-left leading-tight lg:grid">
          <span class="truncate text-sm font-medium">{{ me.displayName }}</span>
          <span class="truncate text-xs text-muted-foreground">{{ roleLabel }}</span>
        </span>
        <ChevronsUpDown class="hidden text-muted-foreground lg:block" />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" class="w-64">
      <DropdownMenuLabel class="flex items-center gap-3 py-2 font-normal">
        <UserAvatar :name="me.displayName" :seed="me.userName" />
        <span class="grid min-w-0 leading-tight">
          <span class="truncate text-sm font-medium">{{ me.displayName }}</span>
          <span class="truncate text-xs text-muted-foreground">{{ me.userName }} · {{ roleLabel }}</span>
        </span>
      </DropdownMenuLabel>
      <DropdownMenuSeparator />
      <DropdownMenuItem as-child>
        <RouterLink :to="{ name: 'profile' }">
          <UserRound />
          {{ t('shell.profile') }}
        </RouterLink>
      </DropdownMenuItem>
      <template v-if="session.isAdmin">
        <DropdownMenuSeparator />
        <DropdownMenuGroup>
          <DropdownMenuLabel class="text-xs font-medium text-muted-foreground">
            {{ t('shell.developer') }}
          </DropdownMenuLabel>
          <DropdownMenuItem as-child>
            <RouterLink :to="{ name: 'ui-kit' }">
              <Palette />
              {{ t('shell.uiKit') }}
            </RouterLink>
          </DropdownMenuItem>
        </DropdownMenuGroup>
      </template>
      <DropdownMenuSeparator />
      <DropdownMenuItem @select="signOut">
        <LogOut />
        {{ t('shell.logout') }}
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
