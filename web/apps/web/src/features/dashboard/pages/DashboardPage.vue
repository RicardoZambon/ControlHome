<script setup lang="ts">
import { Alert, AlertDescription, Badge, Button, Card, Skeleton, cn } from '@controlhome/ui'
import {
  ArrowUpRight,
  ChartLine,
  Clock,
  Coins,
  Globe,
  Languages,
  Layers,
  MapPin,
  PlugZap,
  RefreshCw,
  UserRound,
  UsersRound,
  Zap,
} from '@lucide/vue'
import { useNow } from '@vueuse/core'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import FloorPlanArt from '../../../components/brand/FloorPlanArt.vue'
import { dayPartOf, formatTimeZoneName, timeZoneOffset } from '../../../lib/format'
import { useHomeStore } from '../../../stores/home'
import { useSessionStore } from '../../../stores/session'

const { t, locale } = useI18n()
const session = useSessionStore()
const homeStore = useHomeStore()
const now = useNow({ interval: 30_000 })

onMounted(() => void homeStore.load())

const home = computed(() => homeStore.home)
const timeZone = computed(() => home.value?.timeZoneId)
const firstName = computed(() => session.me?.displayName.trim().split(/\s+/)[0] ?? '')
const greeting = computed(() =>
  t(`dashboard.greeting.${dayPartOf(now.value, timeZone.value)}`, { name: firstName.value }),
)

function formatIn(options: Intl.DateTimeFormatOptions): string {
  try {
    return new Intl.DateTimeFormat(locale.value, { ...options, timeZone: timeZone.value }).format(now.value)
  } catch {
    return new Intl.DateTimeFormat(locale.value, options).format(now.value)
  }
}
const time = computed(() => formatIn({ hour: '2-digit', minute: '2-digit' }))
const date = computed(() => formatIn({ weekday: 'long', day: 'numeric', month: 'long' }))
const isoNow = computed(() => now.value.toISOString())

const details = computed(() => {
  const value = home.value
  if (!value) return []
  const number = new Intl.NumberFormat(locale.value, { maximumFractionDigits: 4 })
  let price = t('common.notSet')
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
      value: `${formatTimeZoneName(value.timeZoneId)} · ${timeZoneOffset(value.timeZoneId, now.value)}`,
    },
    {
      icon: MapPin,
      label: t('dashboard.details.location'),
      value:
        value.latitude !== null && value.longitude !== null
          ? `${number.format(value.latitude)}, ${number.format(value.longitude)}`
          : t('common.notSet'),
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

const startCards = computed(() =>
  [
    {
      to: { name: 'profile' },
      icon: UserRound,
      title: t('dashboard.start.profileTitle'),
      description: t('dashboard.start.profileDescription'),
      tone: 'bg-room-5 text-room-5-foreground',
      visible: true,
    },
    {
      to: { name: 'users' },
      icon: UsersRound,
      title: t('dashboard.start.usersTitle'),
      description: t('dashboard.start.usersDescription'),
      tone: 'bg-room-2 text-room-2-foreground',
      visible: session.isAdmin,
    },
  ].filter((card) => card.visible),
)

const comingNext = computed(() => [
  {
    icon: Layers,
    title: t('dashboard.next.floorsTitle'),
    description: t('dashboard.next.floorsDescription'),
    badge: t('dashboard.next.badge'),
    tone: 'bg-room-3/15 text-foreground',
  },
  {
    icon: PlugZap,
    title: t('dashboard.next.devicesTitle'),
    description: t('dashboard.next.devicesDescription'),
    badge: t('dashboard.next.planned'),
    tone: 'bg-room-1/15 text-foreground',
  },
  {
    icon: ChartLine,
    title: t('dashboard.next.energyTitle'),
    description: t('dashboard.next.energyDescription'),
    badge: t('dashboard.next.planned'),
    tone: 'bg-state-energy/15 text-foreground',
  },
])
</script>

<template>
  <div class="grid gap-10">
    <!-- Hero: greeting in the home's time zone, and where the floor plan will be. -->
    <section
      class="relative overflow-hidden rounded-2xl border bg-card shadow-sm"
      aria-labelledby="dashboard-greeting"
    >
      <div
        class="pointer-events-none absolute inset-0 bg-radial-[at_100%_0%] from-accent via-transparent to-transparent"
      />
      <div
        class="relative grid gap-8 p-6 sm:p-8 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)] lg:items-center lg:p-10"
      >
        <div class="space-y-4">
          <p class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
            <Clock class="size-4" aria-hidden="true" />
            <span class="sr-only">{{ t('dashboard.localTime') }}:</span>
            <time :datetime="isoNow" class="font-medium text-foreground">{{ time }}</time>
            <span aria-hidden="true">·</span>
            <span class="first-letter:uppercase">{{ date }}</span>
          </p>
          <h1
            id="dashboard-greeting"
            class="font-serif text-4xl leading-[1.05] font-medium tracking-tight text-balance sm:text-5xl"
          >
            {{ greeting }}
          </h1>
          <Skeleton v-if="!home && homeStore.loading" class="h-5 w-72 max-w-full" />
          <p v-else-if="home" class="max-w-xl text-lg text-pretty text-muted-foreground">
            {{ t('dashboard.welcome', { home: home.name }) }}
          </p>
        </div>
        <div class="relative rounded-xl border bg-background/70 p-4 sm:p-5">
          <FloorPlanArt variant="outline" class="opacity-60" />
          <div class="absolute inset-0 flex items-center justify-center p-6">
            <div
              class="max-w-60 space-y-1 rounded-lg bg-card/90 px-4 py-3 text-center shadow-sm backdrop-blur-sm"
            >
              <p class="font-serif text-lg leading-snug font-medium text-balance">
                {{ t('dashboard.planTeaser') }}
              </p>
              <p class="text-xs text-pretty text-muted-foreground">{{ t('dashboard.planTeaserDetail') }}</p>
            </div>
          </div>
        </div>
      </div>
    </section>

    <div class="grid gap-10 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:gap-8">
      <section aria-labelledby="dashboard-start" class="space-y-4">
        <h2 id="dashboard-start" class="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
          {{ t('dashboard.start.heading') }}
        </h2>
        <div class="grid gap-4 sm:grid-cols-2">
          <RouterLink
            v-for="(card, index) in startCards"
            :key="card.title"
            :to="card.to"
            :class="
              cn(
                'group relative flex flex-col gap-4 rounded-xl border bg-card p-5 shadow-xs transition-all outline-none hover:-translate-y-0.5 hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
                'motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-bottom-2 motion-safe:fill-mode-both motion-safe:duration-500',
                index === 1 && 'motion-safe:delay-100',
              )
            "
          >
            <div class="flex items-start justify-between">
              <span
                :class="cn('flex size-10 items-center justify-center rounded-lg', card.tone)"
                aria-hidden="true"
              >
                <component :is="card.icon" class="size-5" />
              </span>
              <ArrowUpRight
                class="size-4 text-muted-foreground transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5"
                aria-hidden="true"
              />
            </div>
            <div class="space-y-1">
              <h3 class="font-medium">{{ card.title }}</h3>
              <p class="text-sm text-pretty text-muted-foreground">{{ card.description }}</p>
            </div>
          </RouterLink>
        </div>

        <h2
          id="dashboard-next"
          class="pt-6 text-sm font-semibold tracking-wide text-muted-foreground uppercase"
        >
          {{ t('dashboard.next.heading') }}
        </h2>
        <ol class="grid gap-3" aria-labelledby="dashboard-next">
          <li
            v-for="item in comingNext"
            :key="item.title"
            class="flex items-start gap-4 rounded-xl border bg-card/60 p-4"
          >
            <span
              :class="cn('flex size-9 shrink-0 items-center justify-center rounded-lg', item.tone)"
              aria-hidden="true"
            >
              <component :is="item.icon" class="size-4.5" />
            </span>
            <div class="min-w-0 flex-1 space-y-1">
              <div class="flex flex-wrap items-center gap-2">
                <h3 class="font-medium">{{ item.title }}</h3>
                <Badge variant="outline" class="text-muted-foreground">{{ item.badge }}</Badge>
              </div>
              <p class="text-sm text-pretty text-muted-foreground">{{ item.description }}</p>
            </div>
          </li>
        </ol>
      </section>

      <section aria-labelledby="dashboard-details" class="space-y-4">
        <h2
          id="dashboard-details"
          class="text-sm font-semibold tracking-wide text-muted-foreground uppercase"
        >
          {{ t('dashboard.details.heading') }}
        </h2>
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
              <dt class="flex w-36 shrink-0 items-start gap-3 text-sm text-muted-foreground">
                <component :is="row.icon" class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                {{ row.label }}
              </dt>
              <dd class="min-w-0 text-sm font-medium wrap-break-word">{{ row.value }}</dd>
            </div>
          </dl>
          <div v-else class="grid gap-4 p-5">
            <Skeleton v-for="index in 5" :key="index" class="h-4 w-full" />
          </div>
        </Card>
      </section>
    </div>
  </div>
</template>
