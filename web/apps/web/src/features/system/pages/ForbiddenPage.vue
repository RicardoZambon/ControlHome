<script setup lang="ts">
import { LockKeyhole } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import StatusMessage from '../components/StatusMessage.vue'

const { t } = useI18n()
</script>

<template>
  <StatusMessage
    :icon="LockKeyhole"
    :code="t('system.forbidden.code')"
    :heading="t('system.forbidden.heading')"
    :description="t('system.forbidden.description')"
    :back="t('system.forbidden.back')"
  />
</template>
