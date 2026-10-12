<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import BrandMark from './BrandMark.vue'

const props = defineProps<{ class?: HTMLAttributes['class']; subtitle?: string }>()
const { t } = useI18n()
</script>

<template>
  <span :class="cn('inline-flex items-center gap-3', props.class)">
    <BrandMark />
    <span class="grid leading-tight">
      <span class="font-serif text-lg font-semibold tracking-tight">{{ t('app.name') }}</span>
      <span v-if="subtitle" class="truncate text-xs text-muted-foreground">{{ subtitle }}</span>
    </span>
  </span>
</template>
