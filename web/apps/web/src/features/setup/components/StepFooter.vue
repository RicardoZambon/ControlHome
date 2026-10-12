<script setup lang="ts">
import { Button } from '@controlhome/ui'
import { ArrowLeft, ArrowRight } from '@lucide/vue'
import { useI18n } from 'vue-i18n'

/** Back / Continue row of a wizard step (Continue submits the step's form). The large size on purpose: the wizard's main action. */
defineProps<{ canGoBack?: boolean; busy?: boolean; nextLabel?: string; busyLabel?: string }>()
const emit = defineEmits<{ back: [] }>()
const { t } = useI18n()
</script>

<template>
  <div class="flex flex-col-reverse gap-3 border-t pt-6 sm:flex-row sm:items-center sm:justify-between">
    <Button v-if="canGoBack" type="button" variant="ghost" size="lg" :disabled="busy" @click="emit('back')">
      <ArrowLeft />
      {{ t('common.back') }}
    </Button>
    <span v-else class="hidden sm:block" />
    <Button type="submit" size="lg" :loading="busy" class="sm:min-w-40">
      {{ busy ? (busyLabel ?? t('common.saving')) : (nextLabel ?? t('common.continue')) }}
      <ArrowRight />
    </Button>
  </div>
</template>
