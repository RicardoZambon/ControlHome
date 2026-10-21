<script setup lang="ts">
import { getDashboard, getFloor, listFloors, type DashboardDto, type FloorDto } from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Skeleton, cn } from '@controlhome/ui'
import {
  Activity,
  Blocks,
  Check,
  ChevronRight,
  Clock,
  Layers,
  MapPin,
  RefreshCw,
  Star,
  WifiOff,
  Zap,
} from '@lucide/vue'
import { useNow } from '@vueuse/core'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import DeviceTile from '../../../components/devices/DeviceTile.vue'
import FloorPreview from '../../../components/floorplan/FloorPreview.vue'
import { floorEyebrow, primaryFloor, totalAreaM2 } from '../../../components/floorplan/floors'
import { formatArea } from '../../../components/floorplan/measure'
import { planMarkers } from '../../../components/floorplan/placements'
import { statusStyle } from '../../../components/integrations/integrationStatus'
import { useEndpointSummary } from '../../../composables/useEndpointSummary'
import { useProblem } from '../../../composables/useProblem'
import { dayPartOf, formatRelative } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { useHomeStore } from '../../../stores/home'
import { useRealtimeStore } from '../../../stores/realtime'
import { useSessionStore } from '../../../stores/session'
import HomeDetails from '../components/HomeDetails.vue'
import RoomCard from '../components/RoomCard.vue'
import StatTile from '../components/StatTile.vue'
import { formatPower, liveSum, roomLive, setupProgress } from '../dashboardLive'

/**
 * The home page (Dashboard v1): a greeting in the home's time zone beside the first floor's plan with its devices live
 * on it; the home at a glance (power drawn now, what is on, what is offline, how the integrations are doing); the
 * favorites as device tiles; every room with what is on, its temperature and power; the offline devices and the
 * integrations' health. Everything stays live from the hub (`GET /api/dashboard` lists the attributes behind each
 * figure). An empty home gets the first steps instead.
 */
const { t, te, locale } = useI18n()
const session = useSessionStore()
const homeStore = useHomeStore()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const problem = useProblem()
const { summaryOf } = useEndpointSummary()
const now = useNow({ interval: 30_000 })

// --- Data ------------------------------------------------------------------------------------------------------------
const dashboard = shallowRef<DashboardDto | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)

async function loadDashboard(quiet = false): Promise<void> {
  if (!quiet) loading.value = true
  try {
    dashboard.value = unwrap(await getDashboard())
    loadError.value = null
  } catch (error) {
    if (!quiet || !dashboard.value) loadError.value = error
  } finally {
    loading.value = false
  }
}

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

onMounted(() => {
  void homeStore.load()
  void devices.load()
  void loadDashboard()
  void loadPlan()
})

// Devices added or removed, availability or integrations changing: fetch the summary again (debounced).
let reloadTimer: ReturnType<typeof setTimeout> | undefined
function scheduleReload(plan = false): void {
  clearTimeout(reloadTimer)
  reloadTimer = setTimeout(() => {
    void loadDashboard(true)
    if (plan) void loadPlan()
  }, 600)
}
watch(
  () => realtime.devicesVersion,
  () => scheduleReload(true),
)
watch(
  () => [...realtime.availability.entries()].map(([id, value]) => `${id}:${value}`).join(),
  () => scheduleReload(),
)
onBeforeUnmount(() => clearTimeout(reloadTimer))

// --- Greeting ----------------------------------------------------------------------------------------------------------
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

// --- The plan ------------------------------------------------------------------------------------------------------------
const planMarkersLive = computed(() =>
  planFloor.value ? planMarkers(planFloor.value.placements, devices.devices) : [],
)
const planSummary = computed(() => {
  const floor = planFloor.value
  if (!floor) return ''
  const parts = [t('floors.roomCount', floor.rooms.length)]
  if (floor.rooms.length > 0) parts.push(formatArea(totalAreaM2(floor), locale.value))
  if (planMarkersLive.value.length > 0) parts.push(t('floors.view.deviceCount', planMarkersLive.value.length))
  return parts.join(' · ')
})

// --- At a glance -----------------------------------------------------------------------------------------------------------
const read = (attributeId: number) => realtime.valueOf(attributeId)

const power = computed(() => {
  const summary = dashboard.value?.power
  if (!summary) return null
  const watts = liveSum(summary.attributeIds, read, summary.watts)
  return watts === null ? null : formatPower(watts, locale.value)
})

const visibleEntries = computed(() =>
  devices.entries.filter((entry) => !entry.endpoint.hidden && !entry.device.isOrphaned),
)
const activeCount = computed(
  () => visibleEntries.value.filter((entry) => summaryOf(entry.device, entry.endpoint).active).length,
)

const offline = computed(() =>
  (dashboard.value?.offlineDevices ?? []).filter((device) => devices.availabilityOf(device) === 'Offline'),
)

const integrations = computed(() =>
  (dashboard.value?.integrations ?? []).map((integration) => {
    const live = devices.integrationStatusOf(integration)
    const status = integration.enabled ? live.status : 'Stopped'
    return { integration, status, style: statusStyle(status) }
  }),
)
const attentionCount = computed(() => integrations.value.filter((entry) => entry.style.attention).length)

const rooms = computed(() =>
  (dashboard.value?.rooms ?? []).map((room) => ({ room, live: roomLive(room, read) })),
)
const multipleFloors = computed(() => new Set(rooms.value.map((entry) => entry.room.floorId)).size > 1)

const favorites = computed(() => {
  const byEndpoint = new Map(devices.entries.map((entry) => [entry.endpoint.id, entry]))
  return (dashboard.value?.favoriteEndpointIds ?? []).flatMap((id) => byEndpoint.get(id) ?? [])
})
const roomName = (roomId: number | null) =>
  roomId === null ? undefined : devices.roomsById.get(roomId)?.name

const progress = computed(() =>
  setupProgress({
    floorCount: dashboard.value?.floorCount ?? 0,
    deviceCount: dashboard.value?.deviceCount ?? 0,
    placements: planFloor.value?.placements.length ?? 0,
  }),
)
const steps = computed(() => [
  {
    id: 'floors',
    done: progress.value.floors,
    icon: Layers,
    title: t('dashboard.start.floorsTitle'),
    description: t('dashboard.start.floorsDescription'),
    to: { name: 'floors' },
    action: session.isAdmin ? t('dashboard.start.floorsAction') : undefined,
  },
  {
    id: 'devices',
    done: progress.value.devices,
    icon: Blocks,
    title: t('dashboard.start.devicesTitle'),
    description: t('dashboard.start.devicesDescription'),
    to: { name: 'integration-catalog' },
    action: session.isAdmin ? t('dashboard.start.devicesAction') : undefined,
  },
  {
    id: 'placed',
    done: progress.value.placed,
    icon: MapPin,
    title: t('dashboard.start.placeTitle'),
    description: t('dashboard.start.placeDescription'),
    to: planFloor.value
      ? { name: 'floor-edit', params: { id: planFloor.value.id }, query: { tab: 'devices' } }
      : { name: 'floors' },
    action:
      session.isAdmin && progress.value.floors && progress.value.devices
        ? t('dashboard.start.placeAction')
        : undefined,
  },
])
</script>

<template>
  <div class="grid gap-10">
    <!-- Hero: greeting in the home's time zone, and the first floor with its devices live on it. -->
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
              ? 'lg:grid-cols-[minmax(0,1fr)_minmax(0,30rem)]'
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

        <RouterLink
          v-if="planFloor"
          :to="{ name: 'floor', params: { id: planFloor.id } }"
          class="group block overflow-hidden rounded-xl border bg-background shadow-xs transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          data-slot="dashboard-plan"
        >
          <FloorPreview :floor="planFloor" :markers="planMarkersLive" />
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

    <Alert v-if="loadError && !dashboard" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('dashboard.loadFailed') }} {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="loadDashboard()">
          <RefreshCw />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <!-- At a glance -->
    <section
      v-if="loading && !dashboard"
      class="grid grid-cols-2 gap-3 lg:grid-cols-4"
      :aria-label="t('common.loading')"
    >
      <Skeleton v-for="index in 4" :key="index" class="h-32 rounded-2xl" />
    </section>
    <section
      v-else-if="dashboard"
      aria-labelledby="dashboard-glance"
      class="grid gap-4"
      data-slot="dashboard-glance"
    >
      <h2 id="dashboard-glance" class="sr-only">{{ t('dashboard.glance.heading') }}</h2>
      <div class="grid grid-cols-2 gap-3 sm:gap-4 lg:grid-cols-4">
        <StatTile
          :icon="Zap"
          :label="t('dashboard.glance.power')"
          :value="power?.value ?? '—'"
          :unit="power?.unit"
          :hint="
            dashboard.power.attributeIds.length > 0
              ? t('dashboard.glance.powerHint', dashboard.power.attributeIds.length)
              : t('dashboard.glance.noPower')
          "
          tone="bg-state-energy/15 text-state-energy"
          data-stat="power"
        />
        <StatTile
          :icon="Activity"
          :label="t('dashboard.glance.active')"
          :value="String(activeCount)"
          :hint="t('dashboard.glance.activeHint', { total: visibleEntries.length }, visibleEntries.length)"
          tone="bg-state-on/15 text-state-on"
          :to="{ name: 'devices' }"
          data-stat="active"
        />
        <StatTile
          :icon="WifiOff"
          :label="t('dashboard.glance.offline')"
          :value="String(offline.length)"
          :hint="offline.length > 0 ? t('dashboard.glance.offlineHint') : t('dashboard.glance.allReachable')"
          :tone="
            offline.length > 0 ? 'bg-state-offline/15 text-state-offline' : 'bg-muted text-muted-foreground'
          "
          :attention="offline.length > 0"
          data-stat="offline"
        />
        <StatTile
          :icon="Blocks"
          :label="t('dashboard.glance.integrations')"
          :value="String(integrations.length)"
          :hint="
            integrations.length === 0
              ? t('dashboard.glance.noIntegrations')
              : attentionCount > 0
                ? t('dashboard.glance.attention', attentionCount)
                : t('dashboard.glance.allConnected')
          "
          :tone="attentionCount > 0 ? 'bg-state-warn/15 text-state-warn' : 'bg-accent text-accent-foreground'"
          :attention="attentionCount > 0"
          :to="session.isAdmin ? { name: 'integrations' } : undefined"
          data-stat="integrations"
        />
      </div>
    </section>

    <!-- First steps (an empty home) -->
    <section
      v-if="dashboard && !progress.done"
      aria-labelledby="dashboard-start"
      class="grid gap-4"
      data-slot="dashboard-start"
    >
      <div class="grid gap-1">
        <h2 id="dashboard-start" class="eyebrow">{{ t('dashboard.start.heading') }}</h2>
        <p class="text-sm text-muted-foreground">
          {{ session.isAdmin ? t('dashboard.start.intro') : t('dashboard.start.introMember') }}
        </p>
      </div>
      <ol class="grid gap-3 md:grid-cols-3">
        <li
          v-for="(step, index) in steps"
          :key="step.id"
          :class="
            cn(
              'flex flex-col gap-4 rounded-2xl border bg-card p-5 shadow-xs',
              step.done && 'border-state-on/40 bg-state-on/5',
            )
          "
          :data-step="step.id"
          :data-done="step.done || undefined"
        >
          <div class="flex items-center justify-between gap-3">
            <span
              :class="
                cn(
                  'flex size-10 items-center justify-center rounded-xl',
                  step.done ? 'bg-state-on text-state-on-foreground' : 'bg-accent text-accent-foreground',
                )
              "
              aria-hidden="true"
            >
              <Check v-if="step.done" class="size-5" />
              <component :is="step.icon" v-else class="size-5" />
            </span>
            <span class="text-xs font-medium text-muted-foreground tabular-nums">
              {{ step.done ? t('dashboard.start.done') : t('dashboard.start.step', { n: index + 1 }) }}
            </span>
          </div>
          <div class="grid gap-1">
            <h3 class="font-medium">{{ step.title }}</h3>
            <p class="text-sm text-muted-foreground">{{ step.description }}</p>
          </div>
          <Button v-if="step.action && !step.done" as-child size="sm" class="mt-auto justify-self-start">
            <RouterLink :to="step.to">{{ step.action }}</RouterLink>
          </Button>
        </li>
      </ol>
    </section>

    <!-- Favorites -->
    <section
      v-if="devices.loaded && visibleEntries.length > 0"
      aria-labelledby="dashboard-favorites"
      class="grid gap-4"
      data-slot="dashboard-favorites"
    >
      <div class="flex items-center justify-between gap-3">
        <h2 id="dashboard-favorites" class="eyebrow">{{ t('dashboard.favorites.heading') }}</h2>
        <RouterLink
          :to="{ name: 'devices' }"
          class="group inline-flex items-center gap-1 rounded-md text-sm font-medium text-primary outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
        >
          {{ t('dashboard.favorites.all') }}
          <ChevronRight class="size-4 transition-transform group-hover:translate-x-0.5" aria-hidden="true" />
        </RouterLink>
      </div>
      <ul v-if="favorites.length > 0" class="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4">
        <li v-for="entry in favorites" :key="entry.endpoint.id">
          <DeviceTile
            :device="entry.device"
            :endpoint="entry.endpoint"
            :room-name="roomName(entry.roomId)"
            class="h-full"
          />
        </li>
      </ul>
      <p
        v-else
        class="flex items-start gap-2 rounded-2xl border border-dashed p-5 text-sm text-muted-foreground"
      >
        <Star class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        {{ t('dashboard.favorites.none') }}
      </p>
    </section>

    <div v-if="dashboard" class="grid gap-10 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:gap-8">
      <!-- Rooms -->
      <section aria-labelledby="dashboard-rooms" class="grid content-start gap-4" data-slot="dashboard-rooms">
        <h2 id="dashboard-rooms" class="eyebrow">{{ t('dashboard.rooms.heading') }}</h2>
        <ul v-if="rooms.length > 0" class="grid gap-3 sm:grid-cols-2">
          <li v-for="entry in rooms" :key="entry.room.id">
            <RoomCard :room="entry.room" :live="entry.live" :show-floor="multipleFloors" />
          </li>
        </ul>
        <div v-else class="grid justify-items-start gap-3 rounded-2xl border border-dashed p-6">
          <p class="text-sm text-muted-foreground">
            {{ session.isAdmin ? t('dashboard.rooms.emptyAdmin') : t('dashboard.rooms.empty') }}
          </p>
          <Button as-child variant="outline" size="sm">
            <RouterLink :to="{ name: 'floors' }">{{ t('dashboard.plan.openFloors') }}</RouterLink>
          </Button>
        </div>
      </section>

      <div class="grid content-start gap-10">
        <!-- Offline devices -->
        <section aria-labelledby="dashboard-offline" class="grid gap-4" data-slot="dashboard-offline">
          <h2 id="dashboard-offline" class="eyebrow">{{ t('dashboard.offline.heading') }}</h2>
          <p v-if="offline.length === 0" class="flex items-center gap-2 text-sm text-muted-foreground">
            <span class="inline-block size-2 rounded-full bg-state-on" aria-hidden="true" />
            {{ t('dashboard.offline.none') }}
          </p>
          <ul v-else class="grid divide-y overflow-hidden rounded-2xl border bg-card shadow-xs">
            <li v-for="device in offline" :key="device.id">
              <RouterLink
                :to="{ name: 'device', params: { id: device.id } }"
                class="group flex items-center gap-3 px-4 py-3 outline-none hover:bg-accent/50 focus-visible:bg-accent/50 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
              >
                <span
                  class="flex size-8 shrink-0 items-center justify-center rounded-full border-2 border-dashed border-state-offline/70 text-state-offline"
                  aria-hidden="true"
                >
                  <WifiOff class="size-3.5" />
                </span>
                <span class="grid min-w-0 flex-1">
                  <span class="truncate text-sm font-medium">{{ device.name }}</span>
                  <span class="truncate text-xs text-muted-foreground">
                    {{ roomName(device.roomId) ?? t('device.noRoom') }}
                    <template v-if="device.lastSeenAtUtc">
                      · {{ formatRelative(device.lastSeenAtUtc, now, locale, t('common.justNow')) }}
                    </template>
                  </span>
                </span>
                <ChevronRight
                  class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                  aria-hidden="true"
                />
              </RouterLink>
            </li>
          </ul>
        </section>

        <!-- Integration health -->
        <section
          v-if="integrations.length > 0"
          aria-labelledby="dashboard-integrations"
          class="grid gap-4"
          data-slot="dashboard-integrations"
        >
          <h2 id="dashboard-integrations" class="eyebrow">{{ t('dashboard.integrations.heading') }}</h2>
          <ul class="flex flex-wrap gap-2">
            <li v-for="entry in integrations" :key="entry.integration.id">
              <component
                :is="session.isAdmin ? 'RouterLink' : 'span'"
                :to="
                  session.isAdmin ? { name: 'integration', params: { id: entry.integration.id } } : undefined
                "
                :class="
                  cn(
                    'inline-flex items-center gap-2 rounded-full border bg-card py-1.5 pr-3.5 pl-3 text-sm shadow-xs',
                    session.isAdmin &&
                      'transition-colors outline-none hover:border-ring/40 focus-visible:ring-2 focus-visible:ring-ring',
                    entry.style.attention && 'border-state-warn/60',
                  )
                "
                data-slot="integration-chip"
                :data-status="entry.status"
              >
                <span class="relative flex size-2" aria-hidden="true">
                  <span
                    v-if="entry.style.pulse"
                    :class="
                      cn(
                        'absolute inset-0 rounded-full opacity-60 motion-safe:animate-ping',
                        entry.style.tone,
                      )
                    "
                  />
                  <span :class="cn('relative size-2 rounded-full', entry.style.tone)" />
                </span>
                <span class="font-medium">{{ entry.integration.name }}</span>
                <span class="text-muted-foreground">{{
                  te(`integrationStatus.${entry.status}`)
                    ? t(`integrationStatus.${entry.status}`)
                    : entry.status
                }}</span>
              </component>
            </li>
          </ul>
        </section>

        <HomeDetails :now="now" />
      </div>
    </div>
  </div>
</template>
