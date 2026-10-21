<script setup lang="ts">
import { Alert, AlertDescription, Badge, Button, Card, Skeleton, cn } from '@controlhome/ui'
import {
  ChartLine,
  ChevronRight,
  Clock,
  Coins,
  Globe,
  Languages,
  Layers,
  MapPin,
  RefreshCw,
  UserRound,
  UsersRound,
  Zap,
} from '@lucide/vue'
import { getFloor, listFloors, type FloorDto } from '@controlhome/api-client'
import { useNow } from '@vueuse/core'
import { computed, onMounted, ref, shallowRef, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import FloorPreview from '../../../components/floorplan/FloorPreview.vue'
import DashboardDevices from '../components/DashboardDevices.vue'
import { floorEyebrow, primaryFloor, totalAreaM2 } from '../../../components/floorplan/floors'
import { formatArea } from '../../../components/floorplan/measure'
import { dayPartOf, timeZoneDisplayName, timeZoneOffset } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
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
      value: timeZoneDisplayName(value.timeZoneId, locale.value, now.value),
      // A real minus sign (U+2212): "GMT−03:00".
      hint: timeZoneOffset(value.timeZoneId, now.value).replace('-', '−'),
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

// --- The floor plan in the hero: the primary floor once one exists, else where it will be ---------------------------------
const planFloor = shallowRef<FloorDto | null>(null)
const planLoading = ref(true)

async function loadPlan(): Promise<void> {
  try {
    const primary = primaryFloor(unwrap(await listFloors()))
    planFloor.value = primary ? unwrap(await getFloor({ path: { id: primary.id } })) : null
  } catch {
    // The placeholder stays: the dashboard is not the place to report this.
    planFloor.value = null
  } finally {
    planLoading.value = false
  }
}
onMounted(() => void loadPlan())

const planSummary = computed(() => {
  const floor = planFloor.value
  if (!floor) return ''
  const rooms = t('floors.roomCount', floor.rooms.length)
  return floor.rooms.length > 0 ? `${rooms} · ${formatArea(totalAreaM2(floor), locale.value)}` : rooms
})

const startCards = computed(() =>
  [
    {
      to: { name: 'floors' },
      icon: Layers,
      title: t('dashboard.start.floorsTitle'),
      description: t('dashboard.start.floorsDescription'),
      tone: 'bg-room-3 text-room-3-foreground',
      visible: true,
    },
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
        :class="
          cn(
            'relative grid gap-6 p-6 sm:gap-8 sm:p-8 lg:items-center lg:p-10',
            planFloor
              ? 'lg:grid-cols-[minmax(0,1fr)_minmax(0,28rem)]'
              : 'lg:grid-cols-[minmax(0,1fr)_minmax(0,20rem)]',
          )
        "
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
            class="font-serif text-4xl leading-[1.05] font-medium tracking-tight sm:text-5xl"
          >
            {{ greeting }}
          </h1>
          <Skeleton v-if="!home && homeStore.loading" class="h-5 w-72 max-w-full" />
          <p v-else-if="home" class="max-w-xl text-lg text-muted-foreground">
            {{ t('dashboard.welcome', { home: home.name }) }}
          </p>
        </div>
        <!-- The home's floor plan (the ground floor), linking to it. -->
        <RouterLink
          v-if="planFloor"
          :to="{ name: 'floor', params: { id: planFloor.id } }"
          class="group block overflow-hidden rounded-xl border bg-background shadow-xs transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
        >
          <FloorPreview :floor="planFloor" />
          <span class="flex items-center gap-3 border-t px-4 py-3">
            <span class="grid min-w-0 flex-1 gap-0.5">
              <span v-if="floorEyebrow(planFloor, t)" class="eyebrow">{{ floorEyebrow(planFloor, t) }}</span>
              <span class="truncate font-serif text-lg leading-snug font-medium">{{ planFloor.name }}</span>
              <span class="text-xs text-muted-foreground tabular-nums">{{ planSummary }}</span>
            </span>
            <span class="flex shrink-0 items-center gap-1 text-sm font-medium text-primary">
              {{ t('dashboard.plan.open') }}
              <ChevronRight
                class="size-4 transition-transform group-hover:translate-x-0.5"
                aria-hidden="true"
              />
            </span>
          </span>
        </RouterLink>
        <Skeleton v-else-if="planLoading" class="hidden min-h-48 rounded-xl sm:block" />
        <template v-else>
          <!-- Where the floor plan will be: one dashed frame (a one-line note on phones). -->
          <div
            class="hidden min-h-48 flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed bg-background/60 p-6 text-center sm:flex"
          >
            <Layers class="size-6 text-muted-foreground" aria-hidden="true" />
            <p class="max-w-64 font-serif text-lg leading-snug font-medium">
              {{ t('dashboard.planTeaser') }}
            </p>
            <p class="max-w-64 text-sm text-muted-foreground">{{ t('dashboard.planTeaserDetail') }}</p>
            <Button as-child variant="outline" size="sm" class="mt-2">
              <RouterLink :to="{ name: 'floors' }">
                {{ session.isAdmin ? t('dashboard.plan.draw') : t('dashboard.plan.openFloors') }}
              </RouterLink>
            </Button>
          </div>
          <RouterLink
            :to="{ name: 'floors' }"
            class="flex items-center gap-2.5 rounded-lg border border-dashed bg-background/60 px-3 py-2.5 text-sm text-muted-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring sm:hidden"
          >
            <Layers class="size-4 shrink-0" aria-hidden="true" />
            <span class="flex-1">{{ t('dashboard.planBanner') }}</span>
            <ChevronRight class="size-4 shrink-0" aria-hidden="true" />
          </RouterLink>
        </template>
      </div>
    </section>

    <!-- Devices: what is on now and the favorites, controllable from here -->
    <DashboardDevices />

    <div class="grid gap-10 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:gap-8">
      <div class="grid content-start gap-10">
        <section aria-labelledby="dashboard-start" class="space-y-4">
          <h2 id="dashboard-start" class="eyebrow">{{ t('dashboard.start.heading') }}</h2>
          <ul class="grid gap-3">
            <li v-for="card in startCards" :key="card.title">
              <RouterLink
                :to="card.to"
                class="group flex items-center gap-4 rounded-xl border bg-card p-4 shadow-xs transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
              >
                <span
                  :class="cn('flex size-10 shrink-0 items-center justify-center rounded-lg', card.tone)"
                  aria-hidden="true"
                >
                  <component :is="card.icon" class="size-5" />
                </span>
                <span class="grid min-w-0 flex-1 gap-0.5">
                  <span class="font-medium">{{ card.title }}</span>
                  <span class="text-sm text-muted-foreground">{{ card.description }}</span>
                </span>
                <ChevronRight
                  class="size-5 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                  aria-hidden="true"
                />
              </RouterLink>
            </li>
          </ul>
        </section>

        <section aria-labelledby="dashboard-next" class="space-y-4">
          <h2 id="dashboard-next" class="eyebrow">{{ t('dashboard.next.heading') }}</h2>
          <ol class="grid gap-3">
            <li
              v-for="item in comingNext"
              :key="item.title"
              class="flex items-start gap-4 rounded-xl border bg-card/60 p-4"
            >
              <span
                :class="cn('flex size-10 shrink-0 items-center justify-center rounded-lg', item.tone)"
                aria-hidden="true"
              >
                <component :is="item.icon" class="size-5" />
              </span>
              <!-- The badge always sits top right of the text, beside the title; the description gets the full width. -->
              <div class="grid min-w-0 flex-1 gap-0.5">
                <div class="flex items-start justify-between gap-3">
                  <h3 class="font-medium">{{ item.title }}</h3>
                  <Badge variant="outline" class="mt-0.5 shrink-0 text-muted-foreground">{{
                    item.badge
                  }}</Badge>
                </div>
                <p class="text-sm text-muted-foreground">{{ item.description }}</p>
              </div>
            </li>
          </ol>
        </section>
      </div>

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
    </div>
  </div>
</template>
