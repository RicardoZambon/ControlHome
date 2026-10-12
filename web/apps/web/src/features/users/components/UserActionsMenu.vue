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

defineProps<{ user: UserDto; busy?: boolean }>()
const emit = defineEmits<{ edit: []; resetPassword: []; toggleActive: []; delete: [] }>()
const { t } = useI18n()
</script>

<template>
  <!-- Not modal: the items open dialogs, and two modal layers changing hands can leave the page inert. -->
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <Button
        variant="ghost"
        size="icon-sm"
        :disabled="busy"
        :aria-label="t('users.actions.menu', { name: user.displayName })"
      >
        <Ellipsis />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" class="w-52">
      <DropdownMenuItem @select="emit('edit')">
        <Pencil />
        {{ t('users.actions.edit') }}
      </DropdownMenuItem>
      <DropdownMenuItem @select="emit('resetPassword')">
        <KeyRound />
        {{ t('users.actions.resetPassword') }}
      </DropdownMenuItem>
      <DropdownMenuItem @select="emit('toggleActive')">
        <UserRoundCheck v-if="user.isDisabled" />
        <UserRoundX v-else />
        {{ user.isDisabled ? t('users.actions.enable') : t('users.actions.disable') }}
      </DropdownMenuItem>
      <DropdownMenuSeparator />
      <DropdownMenuItem variant="destructive" @select="emit('delete')">
        <Trash />
        {{ t('users.actions.delete') }}
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
