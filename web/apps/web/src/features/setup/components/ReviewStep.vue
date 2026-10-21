<script setup lang="ts">
import { Button, Switch, cn } from '@controlhome/ui'
import { FlaskConical, Pencil } from '@lucide/vue'
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import { formatTimeZoneName, timeZoneOffset } from '../../../lib/format'
import { currencyOptions } from '../../../lib/locale-data'
import { parseDecimal } from '../../../lib/validation'
import type { SetupDraft, SetupStepId } from '../setup'
import StepFooter from './StepFooter.vue'

const props = defineProps<{ draft: SetupDraft; busy?: boolean; error?: string }>()
const emit = defineEmits<{
  edit: [step: SetupStepId]
  back: []
  finish: []
  'update:loadDemoHome': [value: boolean]
}>()
const { t, locale } = useI18n()
const demoId = useId()

const numberFormat = computed(() => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 4 }))

const sections = computed(() => {
  const { setupCode, admin, home } = props.draft
  const latitude = parseDecimal(home.latitude)
  const longitude = parseDecimal(home.longitude)
  const price = parseDecimal(home.pricePerKwh)
  const currency = currencyOptions(locale.value).find((option) => option.value === home.currency)?.label
  const money = (value: number) => {
    try {
      return new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: home.currency,
        maximumFractionDigits: 4,
      }).format(value)
    } catch {
      return `${home.currency} ${numberFormat.value.format(value)}`
    }
  }
  return [
    {
      step: 'code' as const,
      title: t('setup.review.code'),
      rows: [{ label: t('setup.review.code'), value: setupCode, mono: true }],
    },
    {
      step: 'admin' as const,
      title: t('setup.review.account'),
      rows: [
        { label: t('setup.review.name'), value: admin.displayName },
        { label: t('setup.review.userName'), value: admin.userName },
        { label: t('setup.review.password'), value: t('setup.review.passwordSet') },
      ],
    },
    {
      step: 'home' as const,
      title: t('setup.review.home'),
      rows: [
        { label: t('setup.review.name'), value: home.name },
        {
          label: t('setup.review.timeZone'),
          value: `${formatTimeZoneName(home.timeZoneId)} (${timeZoneOffset(home.timeZoneId)})`,
        },
        {
          label: t('setup.review.location'),
          value:
            latitude !== null && longitude !== null && !Number.isNaN(latitude) && !Number.isNaN(longitude)
              ? `${numberFormat.value.format(latitude)}, ${numberFormat.value.format(longitude)}`
              : t('common.notSet'),
        },
        {
          label: t('setup.review.language'),
          value: t(home.locale === 'pt-BR' ? 'locales.ptBR' : 'locales.en'),
        },
        { label: t('setup.review.currency'), value: currency ?? home.currency },
        {
          label: t('setup.review.price'),
          value: price !== null && !Number.isNaN(price) ? money(price) : t('common.notSet'),
        },
      ],
    },
  ]
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit.prevent="emit('finish')">
    <FormAlert v-if="error" :message="error" />
    <section
      v-for="section in sections"
      :key="section.step"
      class="rounded-lg border bg-background/60 p-4 sm:p-5"
      :aria-labelledby="`review-${section.step}`"
    >
      <div class="mb-3 flex items-center justify-between gap-3">
        <h3 :id="`review-${section.step}`" class="text-sm font-semibold">{{ section.title }}</h3>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          :aria-label="t('setup.review.editSection', { section: section.title })"
          :disabled="busy"
          @click="emit('edit', section.step)"
        >
          <Pencil />
          {{ t('setup.review.edit') }}
        </Button>
      </div>
      <dl class="grid gap-x-6 gap-y-2.5 text-sm sm:grid-cols-[minmax(0,10rem)_1fr]">
        <template v-for="row in section.rows" :key="row.label">
          <dt class="text-muted-foreground">{{ row.label }}</dt>
          <dd
            :class="[
              'min-w-0 font-medium wrap-break-word',
              'mono' in row && row.mono ? 'font-mono tracking-wider' : '',
            ]"
          >
            {{ row.value }}
          </dd>
        </template>
      </dl>
    </section>
    <!-- Start with the demo home: floors, rooms and simulated devices already in place -->
    <div
      :class="
        cn(
          'flex items-start gap-4 rounded-lg border p-4 transition-colors sm:p-5',
          draft.loadDemoHome ? 'border-primary/50 bg-accent/50' : 'bg-background/60',
        )
      "
      data-slot="demo-home"
    >
      <span
        class="flex size-10 shrink-0 items-center justify-center rounded-lg bg-accent text-accent-foreground"
        aria-hidden="true"
      >
        <FlaskConical class="size-5" />
      </span>
      <div class="grid min-w-0 flex-1 gap-1">
        <label :for="demoId" class="text-sm font-semibold">{{ t('setup.review.demoTitle') }}</label>
        <p :id="`${demoId}-help`" class="text-sm text-muted-foreground">
          {{ t('setup.review.demoDescription') }}
        </p>
      </div>
      <Switch
        :id="demoId"
        :model-value="draft.loadDemoHome"
        :disabled="busy"
        :aria-describedby="`${demoId}-help`"
        @update:model-value="(value: boolean) => emit('update:loadDemoHome', value)"
      />
    </div>

    <StepFooter
      can-go-back
      :busy="busy"
      :next-label="t('setup.review.finish')"
      :busy-label="t('setup.review.finishing')"
      @back="emit('back')"
    />
  </form>
</template>
