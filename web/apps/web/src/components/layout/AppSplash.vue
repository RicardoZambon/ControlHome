<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import BrandMark from '../brand/BrandMark.vue'

/** Shown while the first navigation resolves (server state and session). */
const { t } = useI18n()
</script>

<template>
  <div class="flex min-h-svh flex-col items-center justify-center gap-4" role="status">
    <BrandMark class="size-12 rounded-xl motion-safe:animate-pulse" />
    <p class="text-sm text-muted-foreground">{{ t('app.loading') }}</p>
  </div>
</template>
