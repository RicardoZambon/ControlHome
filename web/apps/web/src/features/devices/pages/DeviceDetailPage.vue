<script setup lang="ts">
import {
  getDevice,
  setEndpointFavorite,
  type DeviceDetailDto,
  type EndpointDto,
} from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  Button,
  Dialog,
  DialogScrollContent,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  Skeleton,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
  cn,
  toast,
} from '@controlhome/ui'
import {
  ArrowLeft,
  ChevronDown,
  Ellipsis,
  Pencil,
  RefreshCw,
  Replace,
  SearchX,
  Star,
  Unlink,
} from '@lucide/vue'
import { computed, defineAsyncComponent, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { isHistoryRange, type HistoryRange } from '../../../components/charts/chartOptions'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import StateBadge from '../../../components/devices/StateBadge.vue'
import { CapabilityControl, PowerToggle } from '../../../components/devices/capabilities'
import { layoutOf } from '../../../components/devices/endpointLayout'
import { standsOpen, summarizeEndpoint, type Tone } from '../../../components/devices/deviceState'
import { capabilityLabel } from '../../../components/devices/format'
import { isLightEndpoint } from '../../../components/devices/lightColor'
import { iconToneOf } from '../../../components/devices/tone'
import { humanize } from '../../../components/schema/schema'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime } from '../../../lib/format'
import { errorCodeOf, unwrap } from '../../../lib/problem'
import { endpointName, toSummary, useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'
import { useSessionStore } from '../../../stores/session'
import AttributesTable from '../components/AttributesTable.vue'
import DeviceEditForm from '../components/DeviceEditForm.vue'
import ReplaceDeviceDialog from '../components/ReplaceDeviceDialog.vue'
import { suggestsReplace } from '../replace'

// The charts (ECharts) load with the History tab, not with every device page.
const DeviceHistory = defineAsyncComponent(() => import('../components/DeviceHistory.vue'))

/**
 * One device, control first: its name with a favorite star, then each endpoint's controls as cards (a light's power is
 * the switch in its brightness card's header, an AC's in its climate card's) and its readings, sized to their content.
 * The device's details (vendor, ids, every attribute) sit in a "Device info" section that starts closed; attribute ids
 * are for Admins. The History tab charts what it recorded (24 h, 7 days, 30 days). A device its integration no longer
 * reports (or the same hardware reached twice) says so, and Admins can hand it over to another device. Admins edit the
 * device from the "…" menu. Live like the device grid.
 */
const { t, te, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const session = useSessionStore()
const problem = useProblem()

const deviceId = computed(() => String(route.params.id))
const device = shallowRef<DeviceDetailDto | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)
const notFound = ref(false)

async function load(quiet = false): Promise<void> {
  if (!quiet) loading.value = true
  loadError.value = null
  try {
    const [result] = await Promise.all([getDevice({ path: { id: deviceId.value } }), devices.load()])
    const detail = unwrap(result)
    device.value = detail
    notFound.value = false
    devices.upsertDevice(detail)
  } catch (error) {
    if (errorCodeOf(error) === 'devices.not_found' || errorCodeOf(error) === 'request.not_found') {
      notFound.value = true
      device.value = null
    } else if (!quiet) loadError.value = error
  } finally {
    loading.value = false
  }
}

watch(deviceId, () => void load(), { immediate: true })

// Devices changed somewhere (renamed, removed, new capabilities): fetch this one again.
let reloadTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => realtime.devicesVersion,
  () => {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(() => void load(true), 300)
  },
)
onBeforeUnmount(() => clearTimeout(reloadTimer))

usePageTrail(() =>
  device.value
    ? [{ label: t('devices.title'), to: { name: 'devices' } }, { label: device.value.name }]
    : null,
)

const summary = computed(() => (device.value ? toSummary(device.value) : null))
const availability = computed(() => (device.value ? devices.availabilityOf(device.value) : 'Unknown'))
/**
 * The device's room; a device without one is in the room all its endpoints are in (a plug placed on a plan puts its
 * endpoint in a room, not the device), so the header agrees with its tile and the plan.
 */
const room = computed(() => {
  const current = device.value
  if (!current) return undefined
  const endpointRooms = new Set(current.endpoints.map((endpoint) => endpoint.effectiveRoomId))
  const roomId =
    current.roomId ?? (endpointRooms.size === 1 ? (endpointRooms.values().next().value ?? null) : null)
  return roomId === null ? undefined : devices.roomsById.get(roomId)
})
const canFavorite = computed(() => session.hasRole('Admin', 'Member'))
const multiple = computed(() => (device.value?.endpoints.length ?? 0) > 1)

const endpointViews = computed(() =>
  (device.value?.endpoints ?? []).map((endpoint) => ({ endpoint, layout: layoutOf(endpoint) })),
)

/** The state of the first endpoint, for the header's icon: its tone, and whether it stands open (a lock, a door). */
const headerIcon = computed<{ tone: Tone; open: boolean }>(() => {
  const current = device.value
  if (!current || current.endpoints.length === 0) return { tone: 'neutral', open: false }
  const endpoint = current.endpoints[0]!
  const ids = new Map<string, number>()
  for (const capability of endpoint.capabilities)
    for (const attribute of capability.attributes)
      ids.set(`${capability.capabilityId}.${attribute.key}`, attribute.attributeId)
  const read = (capabilityId: string, key: string) => realtime.valueOf(ids.get(`${capabilityId}.${key}`))
  const state = summarizeEndpoint(endpoint, read, {
    availability: availability.value,
    deviceClass: current.deviceClass,
    locale: locale.value,
    t,
    te,
  })
  const capabilityIds = endpoint.capabilities
    .filter((capability) => !capability.retired)
    .map((capability) => capability.capabilityId)
  return {
    tone: iconToneOf(state.tone, isLightEndpoint(capabilityIds, current.deviceClass)),
    open: state.tone !== 'offline' && standsOpen(capabilityIds, read),
  }
})

// --- Favorites ---------------------------------------------------------------------------------------------------------
const favoriteBusy = ref<string | null>(null)

async function toggleFavorite(endpoint: EndpointDto): Promise<void> {
  if (!device.value) return
  favoriteBusy.value = endpoint.id
  try {
    const saved = unwrap(
      await setEndpointFavorite({ path: { id: endpoint.id }, body: { favorite: !endpoint.favorite } }),
    )
    device.value = {
      ...device.value,
      endpoints: device.value.endpoints.map((entry) => (entry.id === saved.id ? saved : entry)),
    }
    devices.upsertEndpoint(saved)
    toast.success(
      saved.favorite
        ? t('devices.favorite.added', { name: endpointName(device.value, saved) })
        : t('devices.favorite.removed', { name: endpointName(device.value, saved) }),
    )
  } catch (error) {
    problem.toastError(error)
  } finally {
    favoriteBusy.value = null
  }
}

// --- Tabs: controls / history (kept in the address, so a reload or a link keeps them) -------------------------------
const tab = computed<'controls' | 'history'>({
  get: () => (route.query.tab === 'history' ? 'history' : 'controls'),
  set: (value) => {
    void router.replace({ query: { ...route.query, tab: value === 'history' ? 'history' : undefined } })
  },
})
const range = computed<HistoryRange>({
  get: () => (isHistoryRange(route.query.range) ? route.query.range : '24h'),
  set: (value) => {
    void router.replace({ query: { ...route.query, range: value === '24h' ? undefined : value } })
  },
})

// --- Replace (Admin) -----------------------------------------------------------------------------------------------------
const replaceOpen = ref(false)
const showReplace = computed(() => !!device.value && suggestsReplace(device.value))

async function onReplaced(saved: DeviceDetailDto): Promise<void> {
  replaceOpen.value = false
  devices.upsertDevice(saved)
  void devices.load(true)
  toast.success(t('devices.replace.done', { name: saved.name }))
  await router.replace({ name: 'device', params: { id: saved.id } })
}

// --- Edit (Admin) ------------------------------------------------------------------------------------------------------
const editOpen = ref(false)

function onSaved(saved: DeviceDetailDto): void {
  device.value = saved
  devices.upsertDevice(saved)
  editOpen.value = false
  toast.success(t('devices.edit.saved', { name: saved.name }))
}

const info = computed(() => {
  const current = device.value
  if (!current) return []
  const rows: { label: string; value: string; mono?: boolean }[] = [
    { label: t('devices.info.integration'), value: current.integrationName },
    { label: t('devices.info.manufacturer'), value: current.manufacturer ?? t('common.notSet') },
    { label: t('devices.info.model'), value: current.model ?? t('common.notSet') },
    { label: t('devices.info.firmware'), value: current.firmware ?? t('common.notSet') },
    {
      label: t('devices.info.deviceClass'),
      value: current.deviceClass
        ? te(`devices.classes.${current.deviceClass}`)
          ? t(`devices.classes.${current.deviceClass}`)
          : current.deviceClass
        : t('common.notSet'),
    },
    { label: t('devices.info.externalId'), value: current.externalId, mono: true },
    ...Object.entries(current.connections).map(([key, value]) => ({
      label: te(`devices.connections.${key}`) ? t(`devices.connections.${key}`) : humanize(key),
      value,
      mono: true,
    })),
    {
      label: t('devices.info.lastSeen'),
      value: current.lastSeenAtUtc ? formatDateTime(current.lastSeenAtUtc, locale.value) : t('common.notSet'),
    },
    { label: t('devices.info.added'), value: formatDateTime(current.createdAtUtc, locale.value) },
  ]
  return rows
})
</script>

<template>
  <div class="mx-auto w-full max-w-4xl">
    <!-- Back to the grid, and (Admins) the "…" menu: out of the title's way -->
    <div class="mb-4 flex min-h-control items-center justify-between gap-3">
      <RouterLink
        :to="{ name: 'devices' }"
        class="inline-flex items-center gap-1.5 rounded-md text-sm text-muted-foreground transition-colors outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      >
        <ArrowLeft class="size-4" aria-hidden="true" />
        {{ t('devices.detail.back') }}
      </RouterLink>
      <DropdownMenu v-if="device && session.isAdmin">
        <DropdownMenuTrigger as-child>
          <Button variant="ghost" size="icon" class="-mr-2 shrink-0" :aria-label="t('devices.detail.more')">
            <Ellipsis aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem @select="editOpen = true">
            <Pencil aria-hidden="true" />
            {{ t('devices.edit.title') }}
          </DropdownMenuItem>
          <DropdownMenuItem data-slot="replace-device-menu" @select="replaceOpen = true">
            <Replace aria-hidden="true" />
            {{ t('devices.replace.menu') }}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>

    <!-- Loading -->
    <div v-if="loading && !device" class="grid gap-8" role="status" :aria-label="t('common.loading')">
      <div class="flex items-center gap-5">
        <Skeleton class="size-16 rounded-2xl" />
        <div class="grid gap-2">
          <Skeleton class="h-3 w-32" />
          <Skeleton class="h-9 w-64 max-w-full" />
        </div>
      </div>
      <Skeleton class="h-64 rounded-2xl" />
    </div>

    <!-- Gone -->
    <section
      v-else-if="notFound"
      class="grid justify-items-center gap-4 rounded-2xl border border-dashed px-6 py-16 text-center"
    >
      <SearchX class="size-10 text-muted-foreground" aria-hidden="true" />
      <h1 class="font-serif text-3xl font-medium tracking-tight">{{ t('devices.detail.notFound') }}</h1>
      <p class="max-w-md text-muted-foreground">{{ t('errors.devices.not_found') }}</p>
      <Button as-child variant="outline">
        <RouterLink :to="{ name: 'devices' }">{{ t('devices.detail.back') }}</RouterLink>
      </Button>
    </section>

    <Alert v-else-if="loadError" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('devices.loadFailed') }} {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load()">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <template v-else-if="device && summary">
      <!-- Header: the name with its favorite star -->
      <header class="mb-8 flex items-start gap-4 sm:items-center sm:gap-5">
        <DeviceIcon
          size="lg"
          :icon="device.icon"
          :device-class="device.deviceClass"
          :capabilities="
            device.endpoints.flatMap((endpoint) => endpoint.capabilities.map((c) => c.capabilityId))
          "
          :tone="headerIcon.tone"
          :open="headerIcon.open"
          class="max-sm:size-12 max-sm:rounded-xl max-sm:[&_svg]:size-6"
        />
        <div class="grid min-w-0 flex-1 gap-1.5">
          <p class="eyebrow truncate" data-slot="device-room">
            {{ room?.name ?? t('device.noRoom')
            }}<template v-if="room && room.floorName"> · {{ room.floorName }}</template>
          </p>
          <div class="flex min-w-0 items-start gap-1">
            <h1
              class="min-w-0 font-serif text-3xl leading-tight font-medium tracking-tight break-words md:text-4xl"
            >
              {{ device.name }}
            </h1>
            <TooltipProvider v-if="canFavorite && !multiple && device.endpoints[0]" :delay-duration="300">
              <Tooltip>
                <TooltipTrigger as-child>
                  <Button
                    variant="ghost"
                    size="icon"
                    class="shrink-0 rounded-full"
                    :aria-pressed="device.endpoints[0].favorite"
                    :aria-label="t('devices.favorite.label')"
                    :loading="favoriteBusy === device.endpoints[0].id"
                    data-slot="favorite-toggle"
                    @click="toggleFavorite(device.endpoints[0])"
                  >
                    <Star
                      :class="
                        cn(
                          'size-5',
                          device.endpoints[0].favorite
                            ? 'fill-state-energy text-state-energy'
                            : 'text-muted-foreground',
                        )
                      "
                      aria-hidden="true"
                    />
                  </Button>
                </TooltipTrigger>
                <TooltipContent>
                  {{
                    device.endpoints[0].favorite ? t('devices.favorite.remove') : t('devices.favorite.add')
                  }}
                </TooltipContent>
              </Tooltip>
            </TooltipProvider>
          </div>
          <div class="flex flex-wrap items-center gap-2">
            <StateBadge :availability="availability" />
            <StateBadge v-if="device.isOrphaned" :availability="availability" orphaned />
          </div>
        </div>
      </header>

      <p
        v-if="availability === 'Offline'"
        class="mb-8 rounded-xl border-2 border-dashed border-state-offline/70 bg-state-offline/10 px-4 py-3 text-sm"
        role="status"
      >
        {{ t('devices.detail.offline') }}
      </p>

      <!-- No longer reported, or the same hardware twice: hand it over to another device -->
      <div
        v-if="showReplace"
        class="mb-8 flex flex-wrap items-start gap-4 rounded-2xl border border-state-warn/50 bg-state-warn/10 p-5"
        role="status"
        data-slot="orphaned-notice"
      >
        <Unlink class="mt-0.5 size-5 shrink-0 text-state-warn" aria-hidden="true" />
        <div class="grid min-w-0 flex-1 gap-1">
          <p class="font-medium">
            {{ device.isOrphaned ? t('devices.replace.orphanedTitle') : t('devices.replace.duplicateTitle') }}
          </p>
          <p class="text-sm text-muted-foreground">
            {{
              device.isOrphaned
                ? t('devices.replace.orphanedText', { integration: device.integrationName })
                : t('devices.replace.duplicateText')
            }}
          </p>
        </div>
        <Button
          v-if="session.isAdmin"
          variant="outline"
          data-slot="replace-device"
          @click="replaceOpen = true"
        >
          <Replace aria-hidden="true" />
          {{ t('devices.replace.action') }}
        </Button>
      </div>

      <Tabs v-model="tab" class="gap-8">
        <TabsList class="w-full sm:w-auto" data-slot="device-tabs">
          <TabsTrigger value="controls" class="px-4">{{ t('devices.tabs.controls') }}</TabsTrigger>
          <TabsTrigger value="history" class="px-4" data-slot="history-tab">{{
            t('devices.tabs.history')
          }}</TabsTrigger>
        </TabsList>

        <TabsContent value="history" class="mt-0">
          <DeviceHistory v-if="tab === 'history'" v-model:range="range" :device="device" />
        </TabsContent>

        <TabsContent value="controls" class="mt-0">
          <!-- Endpoints and their controls -->
          <div class="grid gap-12">
            <section
              v-for="{ endpoint, layout } in endpointViews"
              :key="endpoint.id"
              :aria-label="endpointName(device, endpoint)"
              data-slot="endpoint"
              :data-endpoint-id="endpoint.id"
            >
              <header v-if="multiple" class="mb-5 flex items-center justify-between gap-3">
                <h2 class="truncate font-serif text-2xl font-medium tracking-tight">
                  {{ endpointName(device, endpoint) }}
                </h2>
                <Button
                  v-if="canFavorite"
                  variant="ghost"
                  size="icon"
                  class="rounded-full"
                  :aria-pressed="endpoint.favorite"
                  :aria-label="t('devices.favorite.toggle', { name: endpointName(device, endpoint) })"
                  :loading="favoriteBusy === endpoint.id"
                  @click="toggleFavorite(endpoint)"
                >
                  <Star
                    :class="
                      cn(
                        'size-5',
                        endpoint.favorite ? 'fill-state-energy text-state-energy' : 'text-muted-foreground',
                      )
                    "
                    aria-hidden="true"
                  />
                </Button>
              </header>

              <div
                v-if="layout.controls.length > 0"
                class="grid items-start gap-4 md:grid-flow-row-dense md:grid-cols-2"
              >
                <section
                  v-for="entry in layout.controls"
                  :key="entry.capability.id"
                  :class="
                    cn('rounded-2xl border bg-card p-5 shadow-xs sm:p-6', entry.full && 'md:col-span-2')
                  "
                  :aria-label="capabilityLabel(entry.capability.capabilityId, t, te)"
                  :data-capability="entry.capability.capabilityId"
                >
                  <div class="mb-5 flex min-h-control items-center justify-between gap-3">
                    <h3 class="eyebrow">{{ capabilityLabel(entry.capability.capabilityId, t, te) }}</h3>
                    <PowerToggle
                      v-if="entry.power"
                      :device="summary"
                      :endpoint="endpoint"
                      :capability="entry.power"
                    />
                  </div>
                  <CapabilityControl :device="summary" :endpoint="endpoint" :capability="entry.capability" />
                </section>
                <!-- Readings in the free column beside a lone control, sized to their content -->
                <div
                  v-if="layout.readingsBeside"
                  :class="
                    cn('grid content-start items-start gap-3', layout.readings.length > 1 && 'grid-cols-2')
                  "
                  data-slot="readings"
                >
                  <CapabilityControl
                    v-for="capability in layout.readings"
                    :key="capability.id"
                    :device="summary"
                    :endpoint="endpoint"
                    :capability="capability"
                  />
                </div>
              </div>

              <div
                v-if="layout.readings.length > 0 && !layout.readingsBeside"
                :class="
                  cn(
                    'grid grid-cols-2 items-start gap-3 sm:grid-cols-3 lg:grid-cols-4',
                    layout.controls.length > 0 && 'mt-4',
                  )
                "
                data-slot="readings"
              >
                <CapabilityControl
                  v-for="capability in layout.readings"
                  :key="capability.id"
                  :device="summary"
                  :endpoint="endpoint"
                  :capability="capability"
                />
              </div>
            </section>
          </div>

          <!-- Device info: closed until asked for -->
          <details class="group mt-12 rounded-2xl border bg-card shadow-xs" data-slot="device-info">
            <summary
              class="flex min-h-control-lg cursor-pointer list-none items-center justify-between gap-3 rounded-2xl px-5 py-3 font-medium outline-none select-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background [&::-webkit-details-marker]:hidden"
            >
              <span class="grid gap-0.5">
                <span class="font-serif text-xl tracking-tight">{{ t('devices.info.title') }}</span>
                <span class="text-sm font-normal text-muted-foreground">{{ t('devices.info.summary') }}</span>
              </span>
              <ChevronDown
                class="size-5 shrink-0 text-muted-foreground transition-transform group-open:rotate-180"
                aria-hidden="true"
              />
            </summary>
            <div class="grid gap-6 border-t px-5 py-5 lg:grid-cols-[16rem_minmax(0,1fr)] lg:items-start">
              <dl class="grid grid-cols-2 content-start gap-x-4 gap-y-3 text-sm lg:grid-cols-1">
                <div v-for="row in info" :key="row.label" class="grid gap-0.5">
                  <dt class="text-muted-foreground">{{ row.label }}</dt>
                  <dd :class="cn('font-medium break-words', row.mono && 'font-mono text-xs')">
                    {{ row.value }}
                  </dd>
                </div>
              </dl>
              <div class="min-w-0">
                <AttributesTable :device="device" :endpoints="device.endpoints" :show-ids="session.isAdmin" />
              </div>
            </div>
          </details>
        </TabsContent>
      </Tabs>

      <Dialog v-model:open="replaceOpen">
        <DialogScrollContent :close-label="t('common.close')" class="sm:max-w-lg">
          <ReplaceDeviceDialog
            v-if="replaceOpen"
            :device="device"
            @replaced="onReplaced"
            @cancel="replaceOpen = false"
          />
        </DialogScrollContent>
      </Dialog>

      <Dialog v-model:open="editOpen">
        <DialogScrollContent :close-label="t('common.close')" class="sm:max-w-lg">
          <DeviceEditForm
            v-if="editOpen"
            :device="device"
            :rooms="devices.rooms"
            @saved="onSaved"
            @cancel="editOpen = false"
          />
        </DialogScrollContent>
      </Dialog>
    </template>
  </div>
</template>
