<script setup lang="ts">
import { Skeleton, cn } from '@controlhome/ui'
import { ChevronRight, Star } from '@lucide/vue'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import DeviceTile from '../../../components/devices/DeviceTile.vue'
import { summarizeEndpoint, type EndpointSummary } from '../../../components/devices/deviceState'
import { TONES } from '../../../components/devices/tone'
import { endpointName, useDevicesStore, type EndpointEntry } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'
import { useSessionStore } from '../../../stores/session'

/**
 * The home page's devices: "Active now" (how many are on, plus what deserves a glance: climate running, locks,
 * anything open or alarming — each a link to the device grid) and the favorites as the same tiles as the device grid.
 * Hidden until the home has devices.
 */
const { t, te, locale } = useI18n()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const session = useSessionStore()

onMounted(() => void devices.load())

/** At most this many facts after the count. */
const NOTABLE_LIMIT = 4

const entries = computed(() => devices.entries.filter((entry) => !entry.endpoint.hidden || session.isAdmin))

function summaryOf(entry: EndpointEntry): EndpointSummary {
  const ids = new Map<string, number>()
  for (const capability of entry.endpoint.capabilities)
    for (const attribute of capability.attributes)
      ids.set(`${capability.capabilityId}.${attribute.key}`, attribute.attributeId)
  return summarizeEndpoint(
    entry.endpoint,
    (capabilityId, key) => realtime.valueOf(ids.get(`${capabilityId}.${key}`)),
    {
      availability: devices.availabilityOf(entry.device),
      deviceClass: entry.device.deviceClass,
      locale: locale.value,
      t,
      te,
    },
  )
}

const summaries = computed(() => entries.value.map((entry) => ({ entry, summary: summaryOf(entry) })))
const activeCount = computed(() => summaries.value.filter(({ summary }) => summary.active).length)

/** Alerts first (danger, warning), then climate that is running, then locks. */
const notable = computed(() => {
  const rank = ({ summary }: { summary: EndpointSummary }) => {
    if (summary.tone === 'danger') return 0
    if (summary.tone === 'warn') return 1
    if (summary.capabilityId === 'thermostat' && summary.active) return 2
    if (summary.capabilityId === 'lock') return 3
    return undefined
  }
  return summaries.value
    .map((item) => ({ ...item, rank: rank(item) }))
    .filter((item): item is typeof item & { rank: number } => item.rank !== undefined)
    .sort((a, b) => a.rank - b.rank)
    .slice(0, NOTABLE_LIMIT)
    .map(({ entry, summary }) => ({
      key: entry.endpoint.id,
      name: endpointName(entry.device, entry.endpoint),
      label: summary.label,
      dot: TONES[summary.tone].dot,
    }))
})

const favorites = computed(() => entries.value.filter((entry) => entry.endpoint.favorite))
const roomName = (roomId: number | null) =>
  roomId === null ? undefined : devices.roomsById.get(roomId)?.name
</script>

<template>
  <div
    v-if="!devices.loaded && devices.loading"
    class="grid gap-4"
    role="status"
    :aria-label="t('common.loading')"
  >
    <Skeleton class="h-14 w-full rounded-xl" />
    <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
      <Skeleton v-for="index in 4" :key="index" class="h-40 rounded-2xl" />
    </div>
  </div>

  <section
    v-else-if="devices.loaded && entries.length > 0"
    aria-labelledby="dashboard-devices"
    class="grid gap-4"
    data-slot="dashboard-devices"
  >
    <div class="flex items-center justify-between gap-3">
      <h2 id="dashboard-devices" class="eyebrow">{{ t('dashboard.devices.heading') }}</h2>
      <RouterLink
        :to="{ name: 'devices' }"
        class="group inline-flex items-center gap-1 rounded-md text-sm font-medium text-primary outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      >
        {{ t('dashboard.devices.all') }}
        <ChevronRight class="size-4 transition-transform group-hover:translate-x-0.5" aria-hidden="true" />
      </RouterLink>
    </div>

    <!-- Active now -->
    <RouterLink
      :to="{ name: 'devices' }"
      class="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-xl border bg-card px-4 py-3 text-sm shadow-xs transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      data-slot="active-now"
    >
      <span class="flex items-center gap-2 font-medium">
        <span
          aria-hidden="true"
          :class="cn('inline-block size-2 rounded-full', activeCount > 0 ? 'bg-state-on' : 'bg-state-off')"
        />
        {{ t('dashboard.devices.active', { count: activeCount }, activeCount) }}
      </span>
      <span
        v-for="item in notable"
        :key="item.key"
        class="flex min-w-0 items-center gap-2 text-muted-foreground"
      >
        <span aria-hidden="true" :class="cn('inline-block size-2 shrink-0 rounded-full', item.dot)" />
        <span class="min-w-0 truncate">
          <span class="text-foreground">{{ item.name }}</span> · {{ item.label }}
        </span>
      </span>
    </RouterLink>

    <!-- Favorites -->
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
    <p v-else class="flex items-start gap-2 text-sm text-muted-foreground">
      <Star class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      {{ t('dashboard.devices.noFavorites') }}
    </p>
  </section>
</template>
