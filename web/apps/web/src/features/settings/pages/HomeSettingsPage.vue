<script setup lang="ts">
import { updateHome, type HomeDto } from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Card, CardContent, Skeleton, toast } from '@controlhome/ui'
import { RefreshCw, Save } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import { useProblem } from '../../../composables/useProblem'
import type { AppLocale } from '../../../i18n'
import { fieldErrorsOf, unwrap } from '../../../lib/problem'
import { parseDecimal } from '../../../lib/validation'
import { useHomeStore } from '../../../stores/home'
import HomeStep from '../../setup/components/HomeStep.vue'
import type { HomeDraft } from '../../setup/setup'

/**
 * Admin: the home's details — its name, time zone, where it is (for sunrise and sunset), its language, currency and
 * electricity price (what the energy views turn into costs). The same fields as the setup's home step.
 */
const { t, locale } = useI18n()
const problem = useProblem()
const homeStore = useHomeStore()

onMounted(() => void homeStore.load(true))

const home = computed(() => homeStore.home)
/** Numbers as the user types them (in their language's format). */
function draftOf(value: HomeDto): HomeDraft {
  const number = (entry: number | null, digits: number) =>
    entry === null
      ? ''
      : new Intl.NumberFormat(locale.value, { maximumFractionDigits: digits, useGrouping: false }).format(
          entry,
        )
  return {
    name: value.name,
    timeZoneId: value.timeZoneId,
    latitude: number(value.latitude, 6),
    longitude: number(value.longitude, 6),
    locale: value.locale as AppLocale,
    currency: value.currency,
    pricePerKwh: number(value.pricePerKwh, 4),
  }
}

const saving = ref(false)
const error = ref('')
const fieldErrors = ref<Record<string, string>>({})
/** Bumped after a save, so the form starts again from what the server kept. */
const version = ref(0)

async function save(draft: HomeDraft): Promise<void> {
  saving.value = true
  error.value = ''
  fieldErrors.value = {}
  const number = (value: string) => {
    const parsed = parseDecimal(value)
    return parsed === null || Number.isNaN(parsed) ? null : parsed
  }
  try {
    homeStore.home = unwrap(
      await updateHome({
        body: {
          name: draft.name.trim(),
          timeZoneId: draft.timeZoneId,
          latitude: number(draft.latitude),
          longitude: number(draft.longitude),
          locale: draft.locale,
          currency: draft.currency,
          pricePerKwh: number(draft.pricePerKwh),
        },
      }),
    )
    version.value += 1
    toast.success(t('settings.home.saved'))
  } catch (failure) {
    const fields = fieldErrorsOf(failure)
    if (fields) {
      fieldErrors.value = Object.fromEntries(Object.keys(fields).map((key) => [key, t('validation.invalid')]))
    }
    error.value = problem.message(failure)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="mx-auto w-full max-w-3xl">
    <PageHeader
      :eyebrow="t('settings.eyebrow')"
      :title="t('settings.home.title')"
      :description="t('settings.home.description')"
    />

    <Alert v-if="homeStore.error && !home" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(homeStore.error) }}
        <Button variant="outline" size="sm" @click="homeStore.load(true)">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>
    <Skeleton v-else-if="!home" class="h-96 rounded-2xl" />
    <Card v-else class="gap-0 py-0 shadow-xs" data-slot="home-settings">
      <CardContent class="grid gap-6 p-6 sm:p-8">
        <FormAlert v-if="error" :message="error" />
        <HomeStep
          :key="`${home.id}-${version}`"
          :model-value="draftOf(home)"
          :errors="fieldErrors"
          @next="save"
        >
          <template #footer>
            <div class="flex justify-end border-t pt-6">
              <Button type="submit" :loading="saving" data-slot="home-save">
                <Save aria-hidden="true" />
                {{ t('settings.home.save') }}
              </Button>
            </div>
          </template>
        </HomeStep>
      </CardContent>
    </Card>
  </div>
</template>
