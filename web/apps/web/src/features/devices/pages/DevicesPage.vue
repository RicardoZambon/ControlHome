<script setup lang="ts">
import {
  Alert,
  AlertDescription,
  Button,
  Input,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Skeleton,
  cn,
} from '@controlhome/ui'
import { Blocks, CircleDashed, FilterX, House, PlugZap, RefreshCw, Search, X } from '@lucide/vue'
import { computed, onMounted, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter, type LocationQueryValue } from 'vue-router'
import PageHeader from '../../../components/PageHeader.vue'
import DeviceTile from '../../../components/devices/DeviceTile.vue'
import { capabilityLabel } from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'
import { useSessionStore } from '../../../stores/session'
import { summarizeEndpoint } from '../../../components/devices/deviceState'
import { NO_ROOM, filterEntries, groupByRoom, type DeviceFilters } from '../filters'

/**
 * Every device of the home as tiles, grouped by room, with filters by room, kind (capability), integration and a search,
 * kept in the address (so Back returns to the same view). Live: the states come from the realtime store and the list
 * reloads when devices change anywhere.
 */
const { t, te, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const session = useSessionStore()
const problem = useProblem()
const id = useId()

onMounted(() => void devices.load())

const ALL = 'all'
const single = (value: LocationQueryValue | LocationQueryValue[] | undefined) =>
  (Array.isArray(value) ? value[0] : value) || undefined

const filters = computed<DeviceFilters>(() => ({
  room: single(route.query.room),
  capability: single(route.query.capability),
  integration: single(route.query.integration),
  q: single(route.query.q),
}))
const filtering = computed(() => Object.values(filters.value).some(Boolean))

function setFilter(key: keyof DeviceFilters, value: string | undefined): void {
  const query = { ...route.query, [key]: value && value !== ALL ? value : undefined }
  void router.replace({ query })
}

function clearFilters(): void {
  void router.replace({ query: {} })
}

const roomName = (roomId: number) => devices.roomsById.get(roomId)?.name

const visible = computed(() =>
  filterEntries(devices.entries, filters.value, { includeHidden: session.isAdmin, roomName }),
)
const groups = computed(() => groupByRoom(visible.value, devices.rooms))
const multipleFloors = computed(() => new Set(devices.rooms.map((room) => room.floorId)).size > 1)

/** How many tiles of a list are on (lit, running, open…). */
function activeCount(entries: typeof visible.value): number {
  return entries.filter((entry) => {
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
    ).active
  }).length
}

/**
 * How many columns of the page grid a room takes: as many as it has tiles (up to the full width), so rooms with one or
 * two devices sit side by side instead of each leaving a row mostly empty. Its tiles use the same column grid.
 */
function spanOf(count: number): { section: string; list: string } {
  if (count <= 1) return { section: 'col-span-1', list: 'grid-cols-1' }
  if (count === 2) return { section: 'col-span-2', list: 'grid-cols-2' }
  if (count === 3) return { section: 'col-span-2 md:col-span-3', list: 'grid-cols-2 md:grid-cols-3' }
  return { section: 'col-span-full', list: 'grid-cols-2 md:grid-cols-3 xl:grid-cols-4' }
}

const capabilityOptions = computed(() => {
  const ids = new Set<string>()
  for (const entry of devices.entries)
    for (const capability of entry.endpoint.capabilities)
      if (!capability.retired) ids.add(capability.capabilityId)
  return [...ids]
    .map((value) => ({ value, label: capabilityLabel(value, t, te) }))
    .sort((a, b) => a.label.localeCompare(b.label, locale.value))
})
/** Filtering by integration only makes sense with more than one (or when the address asks for it). */
const showIntegrationFilter = computed(() => devices.integrations.length > 1 || !!filters.value.integration)
const hasUnassigned = computed(() => devices.entries.some((entry) => entry.roomId === null))

const empty = computed(() => devices.loaded && devices.entries.length === 0)
const summary = computed(() => {
  const total = visible.value.length
  return t('devices.summary', { count: total, active: activeCount(visible.value) }, total)
})
</script>

<template>
  <div>
    <PageHeader
      :eyebrow="t('devices.eyebrow')"
      :title="t('devices.title')"
      :description="t('devices.description')"
    >
      <template v-if="session.isAdmin && !empty" #actions>
        <Button variant="outline" as-child class="max-sm:w-full">
          <RouterLink :to="{ name: 'integrations' }">
            <Blocks aria-hidden="true" />
            {{ t('devices.manageIntegrations') }}
          </RouterLink>
        </Button>
      </template>
    </PageHeader>

    <Alert v-if="devices.error && !devices.loading" variant="destructive" class="mb-6">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('devices.loadFailed') }} {{ problem.message(devices.error) }}
        <Button variant="outline" size="sm" @click="devices.load(true)">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <!-- Loading -->
    <div v-if="!devices.loaded && devices.loading" class="grid gap-8" :aria-label="t('common.loading')">
      <Skeleton class="h-control w-full max-w-xl" />
      <ul class="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4">
        <li v-for="index in 8" :key="index" class="grid min-h-40 gap-5 rounded-2xl border bg-card p-4">
          <Skeleton class="size-11 rounded-xl" />
          <div class="grid gap-2 self-end">
            <Skeleton class="h-4 w-3/4" />
            <Skeleton class="h-3 w-1/2" />
          </div>
        </li>
      </ul>
    </div>

    <!-- No devices at all -->
    <section
      v-else-if="empty"
      class="relative grid gap-6 overflow-hidden rounded-2xl border bg-card p-6 shadow-sm sm:p-10 md:grid-cols-[minmax(0,1fr)_auto] md:items-center"
      aria-labelledby="devices-empty"
    >
      <div
        class="pointer-events-none absolute inset-0 bg-radial-[at_100%_0%] from-accent via-transparent to-transparent"
        aria-hidden="true"
      />
      <div class="relative grid gap-4">
        <h2 id="devices-empty" class="font-serif text-3xl font-medium tracking-tight">
          {{ t('devices.empty.title') }}
        </h2>
        <p class="max-w-lg text-muted-foreground">
          {{ session.isAdmin ? t('devices.empty.admin') : t('devices.empty.member') }}
        </p>
        <div v-if="session.isAdmin">
          <Button size="lg" as-child>
            <RouterLink :to="{ name: 'integrations' }">
              <PlugZap aria-hidden="true" />
              {{ t('devices.empty.cta') }}
            </RouterLink>
          </Button>
        </div>
      </div>
      <div class="relative hidden grid-cols-3 gap-3 md:grid" aria-hidden="true">
        <span
          v-for="index in 6"
          :key="index"
          class="size-16 rounded-2xl border border-dashed bg-background/60"
          :class="index === 2 && 'border-solid border-state-on/40 bg-state-on/10'"
        />
      </div>
    </section>

    <template v-else-if="devices.loaded">
      <!-- Filters -->
      <div
        :class="
          cn(
            'mb-8 grid grid-cols-2 gap-3',
            showIntegrationFilter
              ? 'md:grid-cols-[minmax(0,1.4fr)_repeat(3,minmax(0,1fr))]'
              : 'md:grid-cols-[minmax(0,1.4fr)_repeat(2,minmax(0,1fr))]',
          )
        "
        role="search"
      >
        <div class="relative col-span-2 md:col-span-1">
          <Search
            class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            :id="`${id}-q`"
            :model-value="filters.q ?? ''"
            type="search"
            :placeholder="t('devices.filters.search')"
            :aria-label="t('devices.filters.search')"
            class="pl-9"
            @update:model-value="(value) => setFilter('q', String(value).trim() ? String(value) : undefined)"
          />
        </div>
        <Select
          :model-value="filters.room ?? ALL"
          @update:model-value="(value) => setFilter('room', value as string)"
        >
          <SelectTrigger class="w-full" :aria-label="t('devices.filters.room')">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ t('devices.filters.allRooms') }}</SelectItem>
            <SelectItem v-for="room in devices.rooms" :key="room.id" :value="String(room.id)">
              {{ multipleFloors ? `${room.name} · ${room.floorName}` : room.name }}
            </SelectItem>
            <SelectItem v-if="hasUnassigned" :value="NO_ROOM">{{ t('device.noRoom') }}</SelectItem>
          </SelectContent>
        </Select>
        <Select
          :model-value="filters.capability ?? ALL"
          @update:model-value="(value) => setFilter('capability', value as string)"
        >
          <SelectTrigger class="w-full" :aria-label="t('devices.filters.capability')">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ t('devices.filters.allCapabilities') }}</SelectItem>
            <SelectItem v-for="option in capabilityOptions" :key="option.value" :value="option.value">
              {{ option.label }}
            </SelectItem>
          </SelectContent>
        </Select>
        <Select
          v-if="showIntegrationFilter"
          :model-value="filters.integration ?? ALL"
          @update:model-value="(value) => setFilter('integration', value as string)"
        >
          <SelectTrigger class="w-full" :aria-label="t('devices.filters.integration')">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">{{ t('devices.filters.allIntegrations') }}</SelectItem>
            <SelectItem
              v-for="integration in devices.integrations"
              :key="integration.id"
              :value="integration.id"
            >
              {{ integration.name }}
            </SelectItem>
          </SelectContent>
        </Select>
      </div>

      <div class="mb-6 flex flex-wrap items-center justify-between gap-3">
        <p class="text-sm text-muted-foreground" role="status">{{ summary }}</p>
        <Button v-if="filtering" variant="ghost" size="sm" @click="clearFilters">
          <X aria-hidden="true" />
          {{ t('devices.filters.clear') }}
        </Button>
      </div>

      <!-- Nothing matches -->
      <div
        v-if="visible.length === 0"
        class="grid justify-items-center gap-3 rounded-2xl border border-dashed px-6 py-14 text-center"
      >
        <FilterX class="size-8 text-muted-foreground" aria-hidden="true" />
        <p class="font-medium">{{ t('devices.filters.none') }}</p>
        <Button variant="outline" @click="clearFilters">{{ t('devices.filters.clear') }}</Button>
      </div>

      <!-- Grouped by room: small rooms share a row, each tile on the same column grid -->
      <div
        v-else
        class="grid grid-cols-2 gap-x-3 gap-y-10 sm:gap-x-4 md:grid-cols-3 xl:grid-cols-4"
        data-slot="room-groups"
      >
        <section
          v-for="group in groups"
          :key="group.roomId ?? 'none'"
          :aria-labelledby="`${id}-room-${group.roomId ?? 'none'}`"
          :class="['min-w-0', spanOf(group.entries.length).section]"
          data-slot="room-group"
        >
          <header class="mb-3 flex min-w-0 items-center gap-3">
            <span
              class="inline-flex size-9 shrink-0 items-center justify-center rounded-xl bg-muted text-muted-foreground [&_svg]:size-4.5"
              aria-hidden="true"
            >
              <House v-if="group.room" /><CircleDashed v-else />
            </span>
            <div class="grid min-w-0">
              <h2
                :id="`${id}-room-${group.roomId ?? 'none'}`"
                class="truncate font-serif text-lg leading-snug font-medium tracking-tight"
              >
                {{ group.room?.name ?? t('device.noRoom') }}
              </h2>
              <span class="truncate text-xs text-muted-foreground tabular-nums">
                <template v-if="group.room && multipleFloors">{{ group.room.floorName }} · </template
                >{{
                  t('devices.groupCount', { count: group.entries.length, active: activeCount(group.entries) })
                }}
              </span>
            </div>
          </header>
          <ul :class="['grid gap-3 sm:gap-4', spanOf(group.entries.length).list]">
            <li
              v-for="(entry, index) in group.entries"
              :key="entry.endpoint.id"
              class="motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-bottom-1 motion-safe:fill-mode-backwards"
              :style="{ animationDelay: `${Math.min(index, 8) * 40}ms` }"
            >
              <DeviceTile
                :device="entry.device"
                :endpoint="entry.endpoint"
                :room-name="entry.roomId === null ? undefined : roomName(entry.roomId)"
                class="h-full"
              />
            </li>
          </ul>
        </section>
      </div>
    </template>
  </div>
</template>
