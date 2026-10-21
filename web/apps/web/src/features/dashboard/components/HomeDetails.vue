<script setup lang="ts">
import { Alert, AlertDescription, Button, Card, Skeleton } from '@controlhome/ui'
import { Coins, Globe, Languages, MapPin, RefreshCw, Zap } from '@lucide/vue'
import { computed, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import { timeZoneDisplayName, timeZoneOffset } from '../../../lib/format'
import { useHomeStore } from '../../../stores/home'

/** The home's settings at a glance: time zone, location, language, currency and electricity price. */
const props = defineProps<{ now: Date }>()

const { t, locale } = useI18n()
const homeStore = useHomeStore()
const home = computed(() => homeStore.home)

interface DetailRow {
  icon: Component
  label: string
  /** Absent: the value isn't set yet (shown muted, so it doesn't read like data). */
  value?: string
  /** A second, muted line (the time zone's UTC offset). */
  hint?: string
  title?: string
}

const details = computed<DetailRow[]>(() => {
  const value = home.value
  if (!value) return []
  const number = new Intl.NumberFormat(locale.value, { maximumFractionDigits: 4 })
  let price: string | undefined
  if (value.pricePerKwh !== null) {
    try {
      const money = new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: value.currency,
        maximumFractionDigits: 4,
      }).format(value.pricePerKwh)
      price = t('dashboard.details.perKwh', { price: money })
    } catch {
      price = t('dashboard.details.perKwh', {
        price: `${value.currency} ${number.format(value.pricePerKwh)}`,
      })
    }
  }
  let currency = value.currency
  try {
    currency = `${value.currency} · ${new Intl.DisplayNames([locale.value], { type: 'currency' }).of(value.currency)}`
  } catch {
    currency = value.currency
  }
  return [
    {
      icon: Globe,
      label: t('dashboard.details.timeZone'),
      value: timeZoneDisplayName(value.timeZoneId, locale.value, props.now),
      // A real minus sign (U+2212): "GMT−03:00".
      hint: timeZoneOffset(value.timeZoneId, props.now).replace('-', '−'),
      title: value.timeZoneId,
    },
    {
      icon: MapPin,
      label: t('dashboard.details.location'),
      value:
        value.latitude !== null && value.longitude !== null
          ? `${number.format(value.latitude)}, ${number.format(value.longitude)}`
          : undefined,
    },
    {
      icon: Languages,
      label: t('dashboard.details.language'),
      value: t(value.locale === 'pt-BR' ? 'locales.ptBR' : 'locales.en'),
    },
    { icon: Coins, label: t('dashboard.details.currency'), value: currency },
    { icon: Zap, label: t('dashboard.details.price'), value: price },
  ]
})
</script>

<template>
  <section aria-labelledby="dashboard-details" class="space-y-4">
    <h2 id="dashboard-details" class="eyebrow">{{ t('dashboard.details.heading') }}</h2>
    <Card class="gap-0 py-2">
      <Alert v-if="homeStore.error && !home" variant="destructive" class="m-4 w-auto">
        <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
          {{ t('dashboard.details.loadFailed') }}
          <Button variant="outline" size="sm" @click="homeStore.load(true)">
            <RefreshCw />
            {{ t('common.retry') }}
          </Button>
        </AlertDescription>
      </Alert>
      <dl v-else-if="home" class="divide-y">
        <div v-for="row in details" :key="row.label" class="flex items-start gap-3 px-5 py-3.5">
          <dt class="flex w-32 shrink-0 items-start gap-3 text-sm text-muted-foreground sm:w-36">
            <component :is="row.icon" class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
            {{ row.label }}
          </dt>
          <dd v-if="row.value" class="grid min-w-0 gap-0.5 text-sm wrap-break-word" :title="row.title">
            <span class="font-medium">{{ row.value }}</span>
            <span v-if="row.hint" class="text-muted-foreground tabular-nums">{{ row.hint }}</span>
          </dd>
          <dd v-else class="text-sm text-muted-foreground">{{ t('common.notSet') }}</dd>
        </div>
      </dl>
      <div v-else class="grid gap-4 p-5">
        <Skeleton v-for="index in 5" :key="index" class="h-4 w-full" />
      </div>
    </Card>
  </section>
</template>
