<script setup lang="ts">
import {
  getDevice,
  setEndpointFavorite,
  type DeviceDetailDto,
  type EndpointCapabilityDto,
  type EndpointDto,
} from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  Button,
  Dialog,
  DialogScrollContent,
  Skeleton,
  cn,
  toast,
} from '@controlhome/ui'
import { ArrowLeft, Pencil, RefreshCw, SearchX, Star } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import StateBadge from '../../../components/devices/StateBadge.vue'
import { CapabilityControl, resolveControl } from '../../../components/devices/capabilities'
import { summarizeEndpoint } from '../../../components/devices/deviceState'
import { capabilityLabel } from '../../../components/devices/format'
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

/**
 * One device: its availability, each endpoint's capability controls (actuators as cards, sensors as readings), and the
 * diagnostics (vendor details and every attribute). Admins rename it and set its room and icon; Members and Admins
 * mark endpoints as favorites. Live like the device grid.
 */
const { t, te, locale } = useI18n()
const route = useRoute()
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
const room = computed(() => (device.value?.roomId ? devices.roomsById.get(device.value.roomId) : undefined))
const canFavorite = computed(() => session.hasRole('Admin', 'Member'))
const multiple = computed(() => (device.value?.endpoints.length ?? 0) > 1)

interface ControlEntry {
  capability: EndpointCapabilityDto
  /** Takes both columns of the control grid. */
  full: boolean
}

interface EndpointLayout {
  controls: ControlEntry[]
  readings: EndpointCapabilityDto[]
  /** The readings fill the free column next to an odd half-width control, else they get their own full row. */
  readingsBeside: boolean
}

/**
 * An endpoint's controls (cards, in the registry's order) and readings (sensor tiles), packed into two columns without
 * holes: the readings sit beside a lone half-width control (the AC's power next to its temperature), and a lone
 * control with nothing beside it takes the whole row.
 */
function layoutOf(endpoint: EndpointDto): EndpointLayout {
  const resolved = endpoint.capabilities
    .filter((capability) => !capability.retired)
    .map((capability) => ({ capability, control: resolveControl(capability.capabilityId) }))
    .sort((a, b) => a.control.options.order - b.control.options.order)
  const controls = resolved
    .filter((entry) => entry.control.options.kind === 'control')
    .map((entry) => ({ capability: entry.capability, full: entry.control.options.wide }))
  const readings = resolved
    .filter((entry) => entry.control.options.kind === 'reading')
    .map((entry) => entry.capability)
  const halves = controls.filter((entry) => !entry.full)
  const readingsBeside = readings.length > 0 && readings.length <= 4 && halves.length % 2 === 1
  if (!readingsBeside && halves.length % 2 === 1) halves[halves.length - 1]!.full = true
  return { controls, readings, readingsBeside }
}

const endpointViews = computed(() =>
  (device.value?.endpoints ?? []).map((endpoint) => ({ endpoint, layout: layoutOf(endpoint) })),
)

/** The state of the first endpoint, for the header's icon. */
const headerTone = computed(() => {
  const current = device.value
  if (!current || current.endpoints.length === 0) return 'neutral' as const
  const endpoint = current.endpoints[0]!
  const ids = new Map<string, number>()
  for (const capability of endpoint.capabilities)
    for (const attribute of capability.attributes)
      ids.set(`${capability.capabilityId}.${attribute.key}`, attribute.attributeId)
  const state = summarizeEndpoint(
    endpoint,
    (capabilityId, key) => realtime.valueOf(ids.get(`${capabilityId}.${key}`)),
    { availability: availability.value, deviceClass: current.deviceClass, locale: locale.value, t, te },
  )
  return state.tone === 'off' ? 'neutral' : state.tone
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
  <div>
    <RouterLink
      :to="{ name: 'devices' }"
      class="mb-6 inline-flex items-center gap-1.5 rounded-md text-sm text-muted-foreground transition-colors outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
    >
      <ArrowLeft class="size-4" aria-hidden="true" />
      {{ t('devices.detail.back') }}
    </RouterLink>

    <!-- Loading -->
    <div v-if="loading && !device" class="grid gap-8" :aria-label="t('common.loading')">
      <div class="flex items-center gap-5">
        <Skeleton class="size-16 rounded-2xl" />
        <div class="grid gap-2">
          <Skeleton class="h-3 w-32" />
          <Skeleton class="h-9 w-64 max-w-full" />
        </div>
      </div>
      <div class="grid gap-4 md:grid-cols-2">
        <Skeleton class="h-64 rounded-2xl" />
        <Skeleton class="h-64 rounded-2xl" />
      </div>
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
      <!-- Header -->
      <header class="mb-10 flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
        <div class="flex min-w-0 items-center gap-5">
          <DeviceIcon
            size="lg"
            :icon="device.icon"
            :device-class="device.deviceClass"
            :capabilities="
              device.endpoints.flatMap((endpoint) => endpoint.capabilities.map((c) => c.capabilityId))
            "
            :tone="headerTone"
          />
          <div class="grid min-w-0 gap-1.5">
            <p class="eyebrow truncate">
              {{ room?.name ?? t('device.noRoom')
              }}<template v-if="room && room.floorName"> · {{ room.floorName }}</template>
            </p>
            <h1 class="font-serif text-3xl leading-tight font-medium tracking-tight break-words md:text-4xl">
              {{ device.name }}
            </h1>
            <div class="flex flex-wrap items-center gap-2">
              <StateBadge :availability="availability" />
              <StateBadge v-if="device.isOrphaned" :availability="availability" orphaned />
            </div>
          </div>
        </div>
        <div class="flex shrink-0 flex-wrap items-center gap-2">
          <Button
            v-if="canFavorite && !multiple && device.endpoints[0]"
            variant="outline"
            :aria-pressed="device.endpoints[0].favorite"
            :loading="favoriteBusy === device.endpoints[0].id"
            class="max-sm:flex-1"
            @click="toggleFavorite(device.endpoints[0])"
          >
            <Star
              :class="cn(device.endpoints[0].favorite && 'fill-state-energy text-state-energy')"
              aria-hidden="true"
            />
            {{ device.endpoints[0].favorite ? t('devices.favorite.on') : t('devices.favorite.off') }}
          </Button>
          <Button v-if="session.isAdmin" variant="outline" class="max-sm:flex-1" @click="editOpen = true">
            <Pencil aria-hidden="true" />
            {{ t('common.edit') }}
          </Button>
        </div>
      </header>

      <p
        v-if="availability === 'Offline'"
        class="mb-8 rounded-xl border border-dashed border-state-offline/60 bg-state-offline/10 px-4 py-3 text-sm"
        role="status"
      >
        {{ t('devices.detail.offline') }}
      </p>

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
              :aria-pressed="endpoint.favorite"
              :aria-label="t('devices.favorite.toggle', { name: endpointName(device, endpoint) })"
              :loading="favoriteBusy === endpoint.id"
              @click="toggleFavorite(endpoint)"
            >
              <Star
                :class="cn(endpoint.favorite && 'fill-state-energy text-state-energy')"
                aria-hidden="true"
              />
            </Button>
          </header>

          <div v-if="layout.controls.length > 0" class="grid gap-4 md:grid-flow-row-dense md:grid-cols-2">
            <section
              v-for="entry in layout.controls"
              :key="entry.capability.id"
              :class="cn('rounded-2xl border bg-card p-5 shadow-xs sm:p-6', entry.full && 'md:col-span-2')"
              :aria-label="capabilityLabel(entry.capability.capabilityId, t, te)"
              :data-capability="entry.capability.capabilityId"
            >
              <h3 class="eyebrow mb-5">{{ capabilityLabel(entry.capability.capabilityId, t, te) }}</h3>
              <CapabilityControl :device="summary" :endpoint="endpoint" :capability="entry.capability" />
            </section>
            <!-- Readings in the free column beside a lone control -->
            <div
              v-if="layout.readingsBeside"
              :class="cn('grid content-start gap-3', layout.readings.length > 1 && 'grid-cols-2')"
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
              cn('grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4', layout.controls.length > 0 && 'mt-4')
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

      <!-- Diagnostics -->
      <section
        class="mt-14 grid gap-4 lg:grid-cols-[20rem_minmax(0,1fr)]"
        aria-labelledby="device-diagnostics"
      >
        <h2 id="device-diagnostics" class="font-serif text-2xl font-medium tracking-tight lg:col-span-2">
          {{ t('devices.diagnostics') }}
        </h2>
        <dl class="grid content-start gap-3 rounded-2xl border bg-card p-5 text-sm shadow-xs">
          <div v-for="row in info" :key="row.label" class="grid gap-0.5">
            <dt class="text-muted-foreground">{{ row.label }}</dt>
            <dd :class="cn('font-medium break-words', row.mono && 'font-mono text-xs')">{{ row.value }}</dd>
          </div>
        </dl>
        <div class="min-w-0 rounded-2xl border bg-card p-2 shadow-xs sm:p-4">
          <AttributesTable :device="device" :endpoints="device.endpoints" />
        </div>
      </section>

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
