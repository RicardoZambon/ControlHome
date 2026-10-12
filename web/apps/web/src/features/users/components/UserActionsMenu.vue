<script setup lang="ts">
import type { UserDto } from '@controlhome/api-client'
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@controlhome/ui'
import { Ellipsis, KeyRound, Pencil, Trash, UserRoundCheck, UserRoundX } from '@lucide/vue'
import { useI18n } from 'vue-i18n'

/**
 * The row actions of a user. Deactivating and deleting ask for confirmation (the page's dialog); neither is offered
 * on your own account, which says why instead.
 */
defineProps<{ user: UserDto; self?: boolean; busy?: boolean }>()
const emit = defineEmits<{ edit: []; resetPassword: []; toggleActive: []; delete: [] }>()
const { t } = useI18n()
</script>

<template>
  <!-- Not modal: the items open dialogs, and two modal layers changing hands can leave the page inert. -->
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground"
        :disabled="busy"
        :aria-label="t('users.actions.menu', { name: user.displayName })"
      >
        <Ellipsis />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" class="w-60">
      <DropdownMenuItem @select="emit('edit')">
        <Pencil />
        {{ t('users.actions.edit') }}
      </DropdownMenuItem>
      <DropdownMenuItem @select="emit('resetPassword')">
        <KeyRound />
        {{ t('users.actions.resetPassword') }}
      </DropdownMenuItem>
      <DropdownMenuItem :disabled="self" class="items-start" @select="emit('toggleActive')">
        <UserRoundCheck v-if="user.isDisabled" class="mt-0.5" />
        <UserRoundX v-else class="mt-0.5" />
        <span class="grid gap-0.5">
          {{ user.isDisabled ? t('users.actions.reactivate') : t('users.actions.deactivate') }}
          <span v-if="self" class="text-xs text-muted-foreground">{{ t('users.selfDeactivate') }}</span>
        </span>
      </DropdownMenuItem>
      <DropdownMenuSeparator />
      <DropdownMenuItem v-if="!self" variant="destructive" @select="emit('delete')">
        <Trash />
        {{ t('users.actions.delete') }}
      </DropdownMenuItem>
      <DropdownMenuItem v-else disabled class="items-start">
        <Trash class="mt-0.5" />
        <span class="grid gap-0.5">
          {{ t('users.actions.delete') }}
          <span class="text-xs text-muted-foreground">{{ t('errors.users.cannot_delete_self') }}</span>
        </span>
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
