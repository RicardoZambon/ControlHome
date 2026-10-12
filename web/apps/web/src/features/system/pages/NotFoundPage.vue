<script setup lang="ts">
import { DoorOpen } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import StatusMessage from '../components/StatusMessage.vue'

const { t } = useI18n()
</script>

<template>
  <StatusMessage
    :icon="DoorOpen"
    :code="t('system.notFound.code')"
    :heading="t('system.notFound.heading')"
    :description="t('system.notFound.description')"
    :back="t('system.notFound.back')"
  />
</template>
