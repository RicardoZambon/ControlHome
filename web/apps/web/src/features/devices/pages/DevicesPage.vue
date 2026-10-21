<script setup lang="ts">
import {
  Alert,
  AlertDescription,
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Input,
  Skeleton,
  cn,
} from '@controlhome/ui'
import { CircleDashed, FilterX, House, ListFilter, PlugZap, RefreshCw, Search, Star, X } from '@lucide/vue'
import { computed, onMounted, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter, type LocationQueryValue } from 'vue-router'
import PageHeader from '../../../components/PageHeader.vue'
import DeviceTile from '../../../components/devices/DeviceTile.vue'
import { summarizeEndpoint } from '../../../components/devices/deviceState'
import { capabilityLabel } from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { useDevicesStore, type EndpointEntry } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'
import { useSessionStore } from '../../../stores/session'
import { NO_ROOM, filterEntries, groupByRoom, type DeviceFilters } from '../filters'

/**
 * Every device of the home as tiles: the favorites first, then each room under a full-width heading, all on the same
 * column grid. Filters: a search, the rooms as a row of chips, and kind and integration in a menu — all kept in the
 * address (so Back returns to the same view). Live: the states come from the realtime store and the list reloads when
 * devices change anywhere.
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
/** Kind or integration chosen in the menu (its button shows a dot). */
const menuFiltering = computed(() => !!filters.value.capability || !!filters.value.integration)

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
/** The favorites, first and with their rooms, while nothing narrows the list. */
const favorites = computed(() =>
  filtering.value ? [] : visible.value.filter((entry) => entry.endpoint.favorite),
)

function isActive(entry: EndpointEntry): boolean {
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
}

/** How many tiles of a list are on (lit, running, open…). */
function activeCount(entries: readonly EndpointEntry[]): number {
  return entries.filter(isActive).length
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

/** The room chips: every room, then "No room" when some device has none. */
const roomChips = computed(() => [
  { value: ALL, label: t('devices.filters.allRooms') },
  ...devices.rooms.map((room) => ({
    value: String(room.id),
    label: multipleFloors.value ? `${room.name} · ${room.floorName}` : room.name,
  })),
  ...(hasUnassigned.value ? [{ value: NO_ROOM, label: t('device.noRoom') }] : []),
])

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
      :description="empty ? undefined : t('devices.description')"
      description-class="max-sm:hidden"
    />

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
    <div
      v-if="!devices.loaded && devices.loading"
      class="grid gap-8"
      role="status"
      :aria-label="t('common.loading')"
    >
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
      <!-- Filters: search and the kind menu, then the rooms as chips (one scrolling row on phones) -->
      <div class="mb-6 grid gap-3" role="search">
        <div class="flex gap-2">
          <div class="relative min-w-0 flex-1 md:max-w-md">
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
              @update:model-value="
                (value) => setFilter('q', String(value).trim() ? String(value) : undefined)
              "
            />
          </div>
          <DropdownMenu>
            <DropdownMenuTrigger as-child>
              <Button
                variant="outline"
                size="icon"
                class="relative shrink-0"
                :aria-label="t('devices.filters.more')"
                data-slot="device-filters"
              >
                <ListFilter aria-hidden="true" />
                <span
                  v-if="menuFiltering"
                  aria-hidden="true"
                  class="absolute -top-0.5 -right-0.5 size-2.5 rounded-full border-2 border-background bg-primary"
                />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" class="max-h-96 w-64 overflow-y-auto">
              <DropdownMenuLabel>{{ t('devices.filters.capability') }}</DropdownMenuLabel>
              <DropdownMenuRadioGroup
                :model-value="filters.capability ?? ALL"
                @update:model-value="(value) => setFilter('capability', value as string)"
              >
                <DropdownMenuRadioItem :value="ALL">{{
                  t('devices.filters.allCapabilities')
                }}</DropdownMenuRadioItem>
                <DropdownMenuRadioItem
                  v-for="option in capabilityOptions"
                  :key="option.value"
                  :value="option.value"
                >
                  {{ option.label }}
                </DropdownMenuRadioItem>
              </DropdownMenuRadioGroup>
              <template v-if="showIntegrationFilter">
                <DropdownMenuSeparator />
                <DropdownMenuLabel>{{ t('devices.filters.integration') }}</DropdownMenuLabel>
                <DropdownMenuRadioGroup
                  :model-value="filters.integration ?? ALL"
                  @update:model-value="(value) => setFilter('integration', value as string)"
                >
                  <DropdownMenuRadioItem :value="ALL">
                    {{ t('devices.filters.allIntegrations') }}
                  </DropdownMenuRadioItem>
                  <DropdownMenuRadioItem
                    v-for="integration in devices.integrations"
                    :key="integration.id"
                    :value="integration.id"
                  >
                    {{ integration.name }}
                  </DropdownMenuRadioItem>
                </DropdownMenuRadioGroup>
              </template>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
        <div
          role="group"
          :aria-label="t('devices.filters.room')"
          class="-mx-4 flex gap-2 overflow-x-auto px-4 py-0.5 sm:-mx-6 sm:px-6 md:mx-0 md:flex-wrap md:px-0"
          data-slot="room-chips"
        >
          <Button
            v-for="chip in roomChips"
            :key="chip.value"
            size="sm"
            variant="outline"
            :aria-pressed="(filters.room ?? ALL) === chip.value"
            :class="
              cn(
                'shrink-0 rounded-full px-3.5',
                (filters.room ?? ALL) === chip.value &&
                  'border-primary bg-primary text-primary-foreground hover:bg-primary/95 hover:text-primary-foreground',
              )
            "
            @click="setFilter('room', chip.value)"
          >
            {{ chip.label }}
          </Button>
        </div>
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

      <div v-else class="grid gap-10" data-slot="room-groups">
        <!-- Favorites: the same tiles, with their rooms -->
        <section v-if="favorites.length > 0" :aria-labelledby="`${id}-favorites`" data-slot="favorites">
          <header class="mb-3 flex min-w-0 items-center gap-3">
            <span
              class="inline-flex size-9 shrink-0 items-center justify-center rounded-xl bg-state-energy/15 text-state-energy [&_svg]:size-4.5"
              aria-hidden="true"
            >
              <Star class="fill-current" />
            </span>
            <h2 :id="`${id}-favorites`" class="font-serif text-lg leading-snug font-medium tracking-tight">
              {{ t('devices.favorites') }}
            </h2>
          </header>
          <ul class="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4">
            <li v-for="entry in favorites" :key="entry.endpoint.id">
              <DeviceTile
                :device="entry.device"
                :endpoint="entry.endpoint"
                :room-name="entry.roomId === null ? undefined : roomName(entry.roomId)"
                class="h-full"
              />
            </li>
          </ul>
        </section>

        <!-- Each room: a full-width heading, its tiles on the shared grid -->
        <section
          v-for="group in groups"
          :key="group.roomId ?? 'none'"
          :aria-labelledby="`${id}-room-${group.roomId ?? 'none'}`"
          class="min-w-0"
          data-slot="room-group"
        >
          <header class="mb-3 flex min-w-0 items-center gap-3">
            <span
              class="inline-flex size-9 shrink-0 items-center justify-center rounded-xl bg-muted text-muted-foreground [&_svg]:size-4.5"
              aria-hidden="true"
            >
              <House v-if="group.room" /><CircleDashed v-else />
            </span>
            <h2
              :id="`${id}-room-${group.roomId ?? 'none'}`"
              class="min-w-0 truncate font-serif text-lg leading-snug font-medium tracking-tight"
            >
              {{ group.room?.name ?? t('device.noRoom') }}
              <span
                v-if="group.room && multipleFloors"
                class="font-sans text-sm font-normal text-muted-foreground"
              >
                · {{ group.room.floorName }}</span
              >
            </h2>
            <span
              v-if="group.entries.length > 1"
              class="ml-auto shrink-0 rounded-full border px-2.5 py-0.5 text-xs text-muted-foreground tabular-nums"
              data-slot="room-active"
            >
              {{
                t('devices.groupActive', { active: activeCount(group.entries) }, activeCount(group.entries))
              }}
            </span>
          </header>
          <ul class="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4">
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
                :show-room="false"
                class="h-full"
              />
            </li>
          </ul>
        </section>
      </div>
    </template>
  </div>
</template>
