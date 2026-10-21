<script setup lang="ts">
import {
  getRetentionSettings,
  updateRetentionSettings,
  type RetentionSettingsDto,
} from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Input, Label, Skeleton, cn, toast } from '@controlhome/ui'
import { Infinity as InfinityIcon, RefreshCw } from '@lucide/vue'
import { computed, onMounted, reactive, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import { useProblem } from '../../../composables/useProblem'
import { fieldErrorsOf, unwrap } from '../../../lib/problem'
import { RETENTION_FIELDS, retentionProblems, tierWidths, type RetentionField } from '../retention'

/**
 * Admin: how long the history is kept (`GET/PUT /api/settings/retention`): every change (used for ranges up to 6 h),
 * the 5-minute averages (up to 7 days), button presses and other events, and the audit log. Hourly averages and hourly
 * energy are kept forever, so long charts and energy totals never lose their past.
 */
const { t, locale } = useI18n()
const problem = useProblem()
const id = useId()

const saved = ref<RetentionSettingsDto | null>(null)
const values = reactive<Record<RetentionField, string>>({
  stateLogDays: '',
  shortTermStatsDays: '',
  deviceEventDays: '',
  auditLogDays: '',
})
const loading = ref(true)
const loadError = ref<unknown>(null)
const saving = ref(false)
const attempted = ref(false)
const serverErrors = ref<Partial<Record<RetentionField, string>>>({})
const formError = ref('')

function fill(settings: RetentionSettingsDto): void {
  saved.value = settings
  for (const field of RETENTION_FIELDS) values[field.key] = String(settings[field.key])
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    fill(unwrap(await getRetentionSettings()))
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
onMounted(() => void load())

const problems = computed(() => retentionProblems(values))
const dirty = computed(
  () =>
    !!saved.value && RETENTION_FIELDS.some((field) => values[field.key] !== String(saved.value![field.key])),
)

function errorOf(key: RetentionField): string {
  // Averages kept shorter than the changes they replace: said at once, not only after Save.
  const local = problems.value[key] === 'shortBelowRaw' || attempted.value ? problems.value[key] : undefined
  if (local === 'shortBelowRaw')
    return t('settings.history.problems.shortBelowRaw', { days: days(values.stateLogDays) })
  if (local) {
    const field = RETENTION_FIELDS.find((entry) => entry.key === key)!
    return t('settings.history.range', { min: field.min, max: field.max })
  }
  return serverErrors.value[key] ?? ''
}

async function save(): Promise<void> {
  attempted.value = true
  formError.value = ''
  serverErrors.value = {}
  if (Object.keys(problems.value).length > 0) return
  saving.value = true
  try {
    const body = Object.fromEntries(
      RETENTION_FIELDS.map((field) => [field.key, Number(values[field.key])]),
    ) as Record<RetentionField, number>
    fill(unwrap(await updateRetentionSettings({ body })))
    attempted.value = false
    toast.success(t('settings.history.saved'))
  } catch (error) {
    const fields = fieldErrorsOf(error)
    if (fields) {
      for (const field of RETENTION_FIELDS)
        if (fields[field.key]) serverErrors.value[field.key] = t('validation.invalid')
    } else {
      formError.value = problem.message(error)
    }
  } finally {
    saving.value = false
  }
}

function reset(): void {
  if (saved.value) fill(saved.value)
  attempted.value = false
  serverErrors.value = {}
}

const days = (value: string) => {
  const number = Number(value)
  return Number.isFinite(number) && value.trim() !== '' ? t('settings.history.days', number) : '—'
}

/**
 * The three tiers a reading goes through as it ages — every change, then 5-minute averages, then hourly averages kept
 * forever — each as long as it lasts (a log scale of days), so equal settings draw an empty middle tier.
 */
const tiers = computed(() => {
  const [raw, short, hourly] = tierWidths(Number(values.stateLogDays), Number(values.shortTermStatsDays))
  return [
    {
      key: 'raw',
      label: t('settings.history.tiers.raw'),
      keep: days(values.stateLogDays),
      tone: 'bg-primary',
      width: raw,
    },
    {
      key: 'short',
      label: t('settings.history.tiers.short'),
      keep: days(values.shortTermStatsDays),
      tone: 'bg-primary/60',
      width: short,
    },
    {
      key: 'hourly',
      label: t('settings.history.tiers.hourly'),
      keep: t('settings.history.forever'),
      tone: 'bg-primary/25',
      width: hourly,
    },
  ]
})
const number = computed(() => new Intl.NumberFormat(locale.value))
</script>

<template>
  <div class="mx-auto w-full max-w-3xl">
    <PageHeader
      :eyebrow="t('settings.eyebrow')"
      :title="t('settings.history.title')"
      :description="t('settings.history.description')"
    />

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <div v-else-if="loading" class="grid gap-4">
      <Skeleton class="h-28 rounded-2xl" />
      <Skeleton class="h-80 rounded-2xl" />
    </div>

    <template v-else>
      <!-- How a reading ages: every change, then 5-minute averages, then hourly averages forever -->
      <section
        class="mb-6 grid gap-4 rounded-2xl border bg-card p-5 shadow-xs sm:p-6"
        aria-labelledby="history-tiers"
      >
        <h2 id="history-tiers" class="eyebrow">{{ t('settings.history.tiers.heading') }}</h2>
        <!-- The tiers in a row, each as long as it lasts -->
        <div
          class="flex h-2 gap-0.5 overflow-hidden rounded-full"
          aria-hidden="true"
          data-slot="retention-bar"
        >
          <span
            v-for="tier in tiers"
            :key="tier.key"
            :class="cn('h-full transition-[width] duration-300', tier.tone)"
            :style="{ width: `${tier.width}%` }"
          />
        </div>
        <ol class="grid gap-3 sm:grid-cols-3 sm:gap-2">
          <li v-for="(tier, index) in tiers" :key="tier.key" class="grid content-start gap-1">
            <span class="flex items-center gap-2 text-sm font-medium">
              <span :class="cn('size-2.5 shrink-0 rounded-full', tier.tone)" aria-hidden="true" />
              <span class="sr-only">{{ index + 1 }}.</span>
              {{ tier.label }}
            </span>
            <span class="flex items-center gap-1.5 text-sm text-muted-foreground tabular-nums">
              <InfinityIcon v-if="tier.key === 'hourly'" class="size-4" aria-hidden="true" />
              {{ tier.keep }}
            </span>
          </li>
        </ol>
      </section>

      <form
        novalidate
        class="grid gap-6 rounded-2xl border bg-card p-5 shadow-xs sm:p-6"
        data-slot="retention-form"
        @submit.prevent="save"
      >
        <FormAlert v-if="formError" :message="formError" />
        <div
          v-for="field in RETENTION_FIELDS"
          :key="field.key"
          class="grid gap-3 sm:grid-cols-[minmax(0,1fr)_10rem] sm:items-start sm:gap-6"
        >
          <div class="grid gap-1">
            <Label :for="`${id}-${field.key}`">{{ t(`settings.history.fields.${field.key}.label`) }}</Label>
            <p :id="`${id}-${field.key}-help`" class="text-sm text-muted-foreground">
              {{ t(`settings.history.fields.${field.key}.help`) }}
            </p>
          </div>
          <div class="grid gap-1.5">
            <div class="relative">
              <Input
                :id="`${id}-${field.key}`"
                v-model="values[field.key]"
                type="number"
                inputmode="numeric"
                :min="field.min"
                :max="field.max"
                step="1"
                class="pr-14 tabular-nums"
                :aria-invalid="errorOf(field.key) ? 'true' : undefined"
                :aria-describedby="`${id}-${field.key}-help${errorOf(field.key) ? ` ${id}-${field.key}-error` : ''}`"
                :data-field="field.key"
              />
              <span
                class="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-sm text-muted-foreground"
                aria-hidden="true"
              >
                {{ t('settings.history.daysUnit') }}
              </span>
            </div>
            <p v-if="errorOf(field.key)" :id="`${id}-${field.key}-error`" class="text-sm text-destructive">
              {{ errorOf(field.key) }}
            </p>
            <p v-else class="text-xs text-muted-foreground tabular-nums">
              {{
                t('settings.history.limits', { min: number.format(field.min), max: number.format(field.max) })
              }}
            </p>
          </div>
        </div>

        <div class="flex flex-wrap justify-end gap-2 border-t pt-5">
          <Button type="button" variant="outline" :disabled="!dirty || saving" @click="reset">
            {{ t('common.cancel') }}
          </Button>
          <Button type="submit" :loading="saving" :disabled="!dirty" data-slot="retention-save">
            {{ saving ? t('common.saving') : t('common.save') }}
          </Button>
        </div>
      </form>
    </template>
  </div>
</template>
