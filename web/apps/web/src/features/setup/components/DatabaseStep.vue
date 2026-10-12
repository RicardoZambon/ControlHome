<script setup lang="ts">
import { Alert, AlertDescription, Button, Card, CardContent, toast } from '@controlhome/ui'
import { Copy, Database, Info, LoaderCircle, RefreshCw } from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * Shown while no database connection is configured: explains CONTROLHOME_DB_CONNECTION. (The in-app database form
 * arrives with the pre-database setup mode in milestone 0.4.)
 */
defineProps<{ checking?: boolean; stillMissing?: boolean }>()
const emit = defineEmits<{ recheck: [] }>()
const { t } = useI18n()

// The Clipboard API exists only in secure contexts (HTTPS or localhost).
const canCopy = typeof navigator !== 'undefined' && !!navigator.clipboard && window.isSecureContext
const copied = ref(false)

async function copyExample(): Promise<void> {
  try {
    await navigator.clipboard.writeText(t('setup.database.example'))
    copied.value = true
    toast.success(t('setup.database.copied'))
  } catch {
    copied.value = false
  }
}
</script>

<template>
  <Card class="gap-0 overflow-hidden py-0">
    <CardContent class="grid gap-6 p-6 sm:p-8">
      <div class="flex items-start gap-4">
        <span
          class="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground"
          aria-hidden="true"
        >
          <Database class="size-5" />
        </span>
        <div class="space-y-1.5">
          <h2 class="font-serif text-2xl font-medium tracking-tight">{{ t('setup.database.title') }}</h2>
          <p class="text-pretty text-muted-foreground">{{ t('setup.database.description') }}</p>
        </div>
      </div>

      <div class="grid gap-3">
        <p class="text-sm">{{ t('setup.database.how') }}</p>
        <div class="relative rounded-lg border bg-muted">
          <pre
            class="overflow-x-auto p-4 pr-12 font-mono text-xs leading-relaxed whitespace-pre-wrap text-foreground"
          ><code>{{ t('setup.database.example') }}</code></pre>
          <Button
            v-if="canCopy"
            type="button"
            variant="ghost"
            size="icon-sm"
            class="absolute top-2 right-2"
            :aria-label="t('setup.database.copy')"
            :title="t('setup.database.copy')"
            @click="copyExample"
          >
            <Copy />
          </Button>
        </div>
        <p class="text-sm text-muted-foreground">{{ t('setup.database.createHint') }}</p>
      </div>

      <Alert>
        <Info />
        <AlertDescription>{{ t('setup.database.later') }}</AlertDescription>
      </Alert>

      <div class="flex flex-col gap-3 border-t pt-6 sm:flex-row sm:items-center sm:justify-between">
        <p v-if="stillMissing && !checking" class="text-sm text-muted-foreground" role="status">
          {{ t('setup.database.stillMissing') }}
        </p>
        <span v-else />
        <Button type="button" size="lg" :disabled="checking" @click="emit('recheck')">
          <LoaderCircle v-if="checking" class="animate-spin" />
          <RefreshCw v-else />
          {{ checking ? t('setup.database.checking') : t('setup.database.checkAgain') }}
        </Button>
      </div>
    </CardContent>
  </Card>
</template>
