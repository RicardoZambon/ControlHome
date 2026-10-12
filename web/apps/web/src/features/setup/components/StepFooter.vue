<script setup lang="ts">
import { Button } from '@controlhome/ui'
import { ArrowLeft, ArrowRight, LoaderCircle } from '@lucide/vue'
import { useI18n } from 'vue-i18n'

/** Back / Continue row of a wizard step (Continue submits the step's form). */
defineProps<{ canGoBack?: boolean; busy?: boolean; nextLabel?: string; busyLabel?: string }>()
const emit = defineEmits<{ back: [] }>()
const { t } = useI18n()
</script>

<template>
  <div class="flex flex-col-reverse gap-3 border-t pt-6 sm:flex-row sm:items-center sm:justify-between">
    <Button v-if="canGoBack" type="button" variant="ghost" :disabled="busy" @click="emit('back')">
      <ArrowLeft />
      {{ t('common.back') }}
    </Button>
    <span v-else class="hidden sm:block" />
    <Button type="submit" size="lg" :disabled="busy" class="sm:min-w-40">
      <LoaderCircle v-if="busy" class="animate-spin" />
      {{ busy ? (busyLabel ?? t('common.saving')) : (nextLabel ?? t('common.continue')) }}
      <ArrowRight v-if="!busy" />
    </Button>
  </div>
</template>
