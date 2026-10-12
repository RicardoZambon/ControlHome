<script setup lang="ts">
import { Button, Card, CardContent, cn } from '@controlhome/ui'
import { DatabaseZap, RefreshCw, TriangleAlert } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

/** The database is configured but not ready yet (connecting, migrating) or failed. The page polls the status. */
const props = defineProps<{ state: string; checking?: boolean }>()
const emit = defineEmits<{ recheck: [] }>()
const { t } = useI18n()

const failed = computed(() => props.state === 'Failed')
const stateLabel = computed(() =>
  ['NotConfigured', 'Connecting', 'Migrating', 'Ready', 'Failed'].includes(props.state)
    ? t(`databaseStates.${props.state}`)
    : props.state,
)
</script>

<template>
  <Card class="gap-0 py-0">
    <CardContent class="grid gap-6 p-6 sm:p-8" role="status" aria-live="polite">
      <div class="flex items-start gap-4">
        <span
          :class="
            cn(
              'flex size-11 shrink-0 items-center justify-center rounded-xl',
              failed ? 'bg-destructive text-destructive-foreground' : 'bg-accent text-accent-foreground',
            )
          "
          aria-hidden="true"
        >
          <TriangleAlert v-if="failed" class="size-5" />
          <DatabaseZap v-else class="size-5 motion-safe:animate-pulse" />
        </span>
        <div class="space-y-1.5">
          <h2 class="font-serif text-2xl font-medium tracking-tight">
            {{ failed ? t('setup.waiting.failedTitle') : t('setup.waiting.title') }}
          </h2>
          <p class="text-pretty text-muted-foreground">
            {{ failed ? t('setup.waiting.failedDescription') : t('setup.waiting.description') }}
          </p>
          <p class="text-sm font-medium">{{ t('setup.waiting.state', { state: stateLabel }) }}</p>
        </div>
      </div>
      <div class="flex justify-end border-t pt-6">
        <Button type="button" variant="outline" :loading="checking" @click="emit('recheck')">
          <RefreshCw />
          {{ checking ? t('setup.database.checking') : t('setup.database.checkAgain') }}
        </Button>
      </div>
    </CardContent>
  </Card>
</template>
