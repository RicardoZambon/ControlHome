<script setup lang="ts">
import { Badge, cn } from '@controlhome/ui'
import { useI18n } from 'vue-i18n'

/** Whether an account can sign in: a dot in the state color plus the word, never color alone. */
defineProps<{ disabled: boolean }>()
const { t } = useI18n()
</script>

<template>
  <Badge variant="outline" :class="cn('gap-1.5 font-normal', disabled && 'text-muted-foreground')">
    <span :class="cn('size-2 rounded-full', disabled ? 'bg-state-off' : 'bg-state-on')" aria-hidden="true" />
    {{ disabled ? t('users.inactive') : t('users.active') }}
  </Badge>
</template>
