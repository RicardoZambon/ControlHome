<script setup lang="ts">
import { getFloor, listFloors, type FloorDto, type FloorSummaryDto } from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  Button,
  Label,
  Popover,
  PopoverContent,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  Skeleton,
  Switch,
  cn,
} from '@controlhome/ui'
import { ArrowLeft, ChevronRight, MapPinned, PencilRuler, RefreshCw } from '@lucide/vue'
import { useMediaQuery } from '@vueuse/core'
import { computed, onMounted, ref, shallowRef, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import PageHeader from '../../../components/PageHeader.vue'
import DeviceQuickPanel from '../../../components/devices/DeviceQuickPanel.vue'
import { formatMeasurement } from '../../../components/devices/format'
import FloorPlanCanvas, { type CanvasPointerHandler } from '../../../components/floorplan/FloorPlanCanvas.vue'
import PlacementLayer from '../../../components/floorplan/PlacementLayer.vue'
import { floorEyebrow, planBounds, sortFloors, totalAreaM2 } from '../../../components/floorplan/floors'
import {
  MARKER_HIT_RADIUS_PX,
  MARKER_HIT_SLOP_PX,
  markerRadiusPx,
} from '../../../components/floorplan/markerTone'
import { formatArea, roomAreaM2 } from '../../../components/floorplan/measure'
import {
  clusterMarkers,
  hitMarker,
  planMarkers,
  type MarkerCluster,
  type PlanMarker,
} from '../../../components/floorplan/placements'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import { useEndpointSummary } from '../../../composables/useEndpointSummary'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { asNumber } from '../../../lib/stateValue'
import { useDevicesStore } from '../../../stores/devices'
import { useSessionStore } from '../../../stores/session'

/**
 * A floor in view mode: the plan (pan, zoom, tap a room) with its devices live on it — tap one for its controls (a
 * popover beside it; a sheet on phones) — its rooms with how many devices each has, the other floors, and (Admin) the
 * editor. The traced plan image stays off unless asked for ("Show original plan"), and then faint.
 */
const { t, locale } = useI18n()
const route = useRoute()
const session = useSessionStore()
const devices = useDevicesStore()
const problem = useProblem()
const { readerOf, summaryOf } = useEndpointSummary()

const floorId = computed(() => Number(route.params.id))
const floor = ref<FloorDto | null>(null)
const floors = ref<FloorSummaryDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)
const selectedRoomId = ref<number | null>(null)
/** The device whose controls are open, and its marker on the plan (the popover's anchor). */
const active = shallowRef<PlanMarker | null>(null)
const anchor = shallowRef<Element | null>(null)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  selectedRoomId.value = null
  active.value = null
  try {
    const [detail, list] = await Promise.all([
      getFloor({ path: { id: floorId.value } }),
      floors.value.length > 0 ? Promise.resolve(null) : listFloors(),
    ])
    floor.value = unwrap(detail)
    if (list) floors.value = sortFloors(unwrap(list))
  } catch (error) {
    loadError.value = error
    floor.value = null
  } finally {
    loading.value = false
  }
}
watch(floorId, load, { immediate: true })
onMounted(() => void devices.load())

// --- Devices on the plan --------------------------------------------------------------------------------------------
const markers = computed(() => (floor.value ? planMarkers(floor.value.placements, devices.devices) : []))
/** What the plan opens on: the rooms and the devices, not the whole floor sheet. */
const fitTo = computed(() => (floor.value ? planBounds(floor.value) : null))

const canvas = ref<InstanceType<typeof FloorPlanCanvas> | null>(null)
const planBox = ref<HTMLElement | null>(null)
const coarse = useMediaQuery('(pointer: coarse)')

/** The markers as drawn at this zoom: those of a room too close to tell apart are one bubble (tap: zoom in). */
const scale = computed(() => canvas.value?.scale ?? 1)
const clusters = computed(() =>
  clusterMarkers(markers.value, scale.value, markerRadiusPx(scale.value, coarse.value)),
)

function open(marker: PlanMarker): void {
  anchor.value = planBox.value?.querySelector(`[data-placement-id="${marker.placement.id}"]`) ?? null
  active.value = marker
}

/** Shows a set of points (cm) as large as they fit, with room around them. */
function zoomToPoints(points: readonly { x: number; y: number }[], margin = 150): void {
  if (points.length === 0) return
  const xs = points.map((point) => point.x)
  const ys = points.map((point) => point.y)
  const x = Math.min(...xs) - margin
  const y = Math.min(...ys) - margin
  canvas.value?.zoomTo({ x, y, width: Math.max(...xs) + margin - x, height: Math.max(...ys) + margin - y })
}

function expand(cluster: MarkerCluster<PlanMarker>): void {
  zoomToPoints(cluster.members.map((member) => ({ x: member.placement.xCm, y: member.placement.yCm })))
}

/** A tap on a device opens its controls, on a group zooms in on it (a tap elsewhere goes on to the rooms). */
const handler: CanvasPointerHandler = {
  tap(pointer) {
    const radius =
      Math.max(markerRadiusPx(scale.value, coarse.value), MARKER_HIT_RADIUS_PX) + MARKER_HIT_SLOP_PX
    const hit = hitMarker(
      clusters.value.map((cluster) => ({
        cluster,
        placement: { xCm: cluster.center.x, yCm: cluster.center.y },
      })),
      pointer.cm,
      radius / scale.value,
    )
    if (!hit) return false
    if (hit.cluster.members.length > 1) expand(hit.cluster)
    else open(hit.cluster.members[0]!)
    return true
  },
}

// The open panel follows the live list (a rename, new endpoints); it closes when its device leaves the plan.
const activeMarker = computed(() =>
  active.value
    ? (markers.value.find((marker) => marker.placement.id === active.value!.placement.id) ?? null)
    : null,
)
const activeRoom = computed(() => {
  const marker = activeMarker.value
  if (!marker) return undefined
  const roomId = marker.placement.roomId ?? marker.endpoint?.effectiveRoomId ?? marker.device.roomId
  return roomId === null ? undefined : devices.roomsById.get(roomId)?.name
})

function onPanelOpenChange(open: boolean): void {
  if (!open) active.value = null
}

// --- Rooms ------------------------------------------------------------------------------------------------------------
/**
 * A room's devices live, as the dashboard counts them: devices (a 3-gang switch is one, on when any of its channels
 * is), how many are on, and the temperature their sensors read.
 */
function liveOf(roomMarkers: readonly PlanMarker[]) {
  const byDevice = new Map<string, boolean>()
  const temperatures: number[] = []
  for (const marker of roomMarkers) {
    const on = marker.endpoints.some((endpoint) => summaryOf(marker.device, endpoint).active)
    byDevice.set(marker.device.id, (byDevice.get(marker.device.id) ?? false) || on)
    for (const endpoint of marker.endpoints) {
      const read = readerOf(endpoint)
      const temperature = asNumber(read('temperature', 'temperature'))
      if (temperature !== undefined) temperatures.push(temperature)
    }
  }
  const total = byDevice.size
  const active = [...byDevice.values()].filter(Boolean).length
  const temperature =
    temperatures.length > 0
      ? formatMeasurement(
          Math.round((temperatures.reduce((sum, value) => sum + value, 0) / temperatures.length) * 10) / 10,
          '°C',
          locale.value,
        )
      : null
  return { total, active, temperature }
}

const rooms = computed(() =>
  (floor.value?.rooms ?? []).map((room) => {
    const roomMarkers = markers.value.filter((marker) => marker.placement.roomId === room.id)
    return {
      room,
      area: formatArea(roomAreaM2(room.polygon), locale.value),
      live: liveOf(roomMarkers),
      Icon: roomIcon(room.icon),
      colors: roomColorClasses(room.colorToken),
    }
  }),
)
/** Devices placed outside every room (a camera in the garden, the porch light). */
const outside = computed(() => markers.value.filter((marker) => marker.placement.roomId === null))
const deviceCount = computed(() => new Set(markers.value.map((marker) => marker.device.id)).size)
const summary = computed(() => {
  if (!floor.value) return ''
  const parts = [t('floors.roomCount', floor.value.rooms.length)]
  if (floor.value.rooms.length > 0) parts.push(formatArea(totalAreaM2(floor.value), locale.value))
  if (deviceCount.value > 0) parts.push(t('floors.view.deviceCount', deviceCount.value))
  return parts.join(' · ')
})

/** What a room's line says: "3 of 6 on · 23.5 °C", or its area while it has no devices. */
function roomLine(entry: (typeof rooms.value)[number]): string {
  if (entry.live.total === 0) return entry.area
  const parts = [
    t('floors.view.activeOf', { active: entry.live.active, total: entry.live.total }, entry.live.total),
  ]
  if (entry.live.temperature) parts.push(entry.live.temperature)
  return parts.join(' · ')
}

usePageTrail(() =>
  floor.value ? [{ label: t('floors.title'), to: { name: 'floors' } }, { label: floor.value.name }] : null,
)

const isLarge = useMediaQuery('(min-width: 1024px)')
const isPhone = useMediaQuery('(max-width: 639px)')
/** The plan box follows the floor's proportions on narrow screens (within reason). */
const planAspect = computed(() => {
  const bounds = fitTo.value
  const ratio = bounds ? bounds.width / Math.max(bounds.height, 1) : 4 / 3
  // A touch taller than what is drawn, for the padding around it and the zoom bar under it on phones.
  return Math.min(1.5, Math.max(0.8, ratio)) * (isPhone.value ? 0.82 : 0.92)
})

const originalId = useId()
const showOriginal = ref(false)
watch(floorId, () => (showOriginal.value = false))

/** A room picked in the list: highlighted and zoomed to on the plan (picked again: the whole plan). */
function toggleRoom(id: number, zoom = false): void {
  selectedRoomId.value = selectedRoomId.value === id ? null : id
  if (!zoom) return
  const room = floor.value?.rooms.find((entry) => entry.id === id)
  if (selectedRoomId.value === null || !room) canvas.value?.fit()
  else zoomToPoints(room.polygon, 80)
}

function showOutside(): void {
  selectedRoomId.value = null
  zoomToPoints(outside.value.map((marker) => ({ x: marker.placement.xCm, y: marker.placement.yCm })))
}
</script>

<template>
  <div>
    <!-- Phones: the top bar shows only the floor's name, so the way back to every floor is here. -->
    <RouterLink
      :to="{ name: 'floors' }"
      class="mb-4 inline-flex items-center gap-1.5 rounded-sm text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring sm:hidden"
    >
      <ArrowLeft class="size-3.5" aria-hidden="true" />
      {{ t('floors.view.back') }}
    </RouterLink>

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <template v-else>
      <!-- Every floor has an eyebrow (its level, or "Floors" when its name says it), so switching never moves the title -->
      <PageHeader
        :eyebrow="(floor && floorEyebrow(floor, t)) || t('floors.title')"
        :title="floor?.name ?? t('floors.view.title')"
        :description="summary || undefined"
        class="pb-6 md:pb-8"
      >
        <!-- Editing on a phone is the rarer task: a quiet button there, the plan comes first. -->
        <template v-if="session.isAdmin && floor" #actions>
          <Button as-child :variant="isPhone ? 'outline' : 'default'" :size="isPhone ? 'sm' : 'default'">
            <RouterLink :to="{ name: 'floor-edit', params: { id: floor.id } }">
              <PencilRuler />
              {{ t('floors.editPlan') }}
            </RouterLink>
          </Button>
        </template>
      </PageHeader>

      <!-- Other floors -->
      <nav
        v-if="floors.length > 1"
        :aria-label="t('floors.view.switchFloor')"
        class="-mx-1 mb-4 flex gap-1 overflow-x-auto px-1 pb-1"
      >
        <RouterLink
          v-for="entry in floors"
          :key="entry.id"
          :to="{ name: 'floor', params: { id: entry.id } }"
          :aria-current="entry.id === floorId ? 'page' : undefined"
          :class="
            cn(
              'inline-flex shrink-0 items-center gap-2 rounded-full border px-3.5 py-1.5 text-sm transition-colors outline-none focus-visible:ring-2 focus-visible:ring-ring',
              entry.id === floorId
                ? 'border-primary bg-primary text-primary-foreground'
                : 'bg-card text-muted-foreground hover:text-foreground',
            )
          "
        >
          <span class="font-medium">{{ entry.name }}</span>
          <span v-if="floorEyebrow(entry, t)" class="text-xs">{{ floorEyebrow(entry, t) }}</span>
        </RouterLink>
      </nav>

      <div class="grid grid-cols-[minmax(0,1fr)] gap-6 lg:grid-cols-[minmax(0,1fr)_18rem]">
        <!-- Phones and tablets: as tall as the floor needs at full width; desktops: most of the screen. -->
        <div
          ref="planBox"
          class="relative w-full overflow-hidden rounded-2xl border shadow-sm lg:h-[min(68dvh,46rem)] lg:min-h-[22rem]"
          :style="isLarge ? undefined : { aspectRatio: planAspect }"
        >
          <Skeleton v-if="loading" class="size-full rounded-none" />
          <FloorPlanCanvas
            v-else-if="floor"
            ref="canvas"
            class="size-full"
            :floor="floor"
            :fit-to="fitTo"
            :background-image="showOriginal ? 'shown' : 'auto'"
            :selected-room-id="selectedRoomId"
            :handler="handler"
            @room-click="(room) => room.id && toggleRoom(room.id)"
            @background-click="selectedRoomId = null"
          >
            <template #overlay="{ scale: overlayScale }">
              <PlacementLayer
                :markers="markers"
                :clusters="clusters"
                :scale="overlayScale"
                :coarse="coarse"
                :selected-id="activeMarker?.placement.id ?? null"
                @activate="open"
                @expand="expand"
              />
            </template>
            <template #controls>
              <div
                v-if="floor.rooms.length === 0"
                class="pointer-events-none absolute inset-0 grid place-items-center p-6"
              >
                <div
                  class="pointer-events-auto grid max-w-xs justify-items-center gap-3 rounded-2xl border bg-background/95 p-6 text-center shadow-lg backdrop-blur-md"
                >
                  <p class="font-serif text-lg font-medium">{{ t('floors.view.noRoomsTitle') }}</p>
                  <p class="text-sm text-muted-foreground">
                    {{ session.isAdmin ? t('floors.view.noRoomsAdmin') : t('floors.view.noRooms') }}
                  </p>
                  <Button v-if="session.isAdmin" as-child size="sm">
                    <RouterLink :to="{ name: 'floor-edit', params: { id: floor.id } }">
                      <PencilRuler />
                      {{ t('floors.view.drawRooms') }}
                    </RouterLink>
                  </Button>
                </div>
              </div>
            </template>
          </FloorPlanCanvas>
        </div>

        <section aria-labelledby="floor-rooms" class="grid content-start gap-3">
          <h2 id="floor-rooms" class="eyebrow">{{ t('floors.view.rooms') }}</h2>
          <div v-if="loading" class="grid gap-2">
            <Skeleton v-for="index in 3" :key="index" class="h-12 w-full" />
          </div>
          <p v-else-if="rooms.length === 0" class="text-sm text-muted-foreground">
            {{ t('floors.view.noRoomsShort') }}
          </p>
          <ul v-else class="grid gap-1.5" data-slot="floor-rooms">
            <li v-for="entry in rooms" :key="entry.room.id">
              <button
                type="button"
                :aria-pressed="selectedRoomId === entry.room.id"
                :class="
                  cn(
                    'group flex w-full items-center gap-3 rounded-xl border bg-card px-3 py-2.5 text-left transition-[border-color,background-color] outline-none hover:border-ring/40 focus-visible:ring-2 focus-visible:ring-ring',
                    selectedRoomId === entry.room.id && 'border-ring/60 bg-accent/50',
                  )
                "
                :data-room-id="entry.room.id"
                @click="toggleRoom(entry.room.id, true)"
              >
                <span
                  :class="
                    cn('flex size-9 shrink-0 items-center justify-center rounded-lg', entry.colors.tint)
                  "
                  aria-hidden="true"
                >
                  <component :is="entry.Icon" class="size-4" />
                </span>
                <span class="grid min-w-0 flex-1">
                  <span class="truncate text-sm font-medium">{{ entry.room.name }}</span>
                  <span
                    :class="
                      cn(
                        'truncate text-xs tabular-nums',
                        entry.live.active > 0 ? 'text-foreground' : 'text-muted-foreground',
                      )
                    "
                    data-slot="room-live"
                  >
                    {{ roomLine(entry) }}
                  </span>
                </span>
                <ChevronRight
                  class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                  aria-hidden="true"
                />
              </button>
            </li>
            <!-- Devices outside every room (the garden, the porch) -->
            <li v-if="outside.length > 0">
              <button
                type="button"
                class="group flex w-full items-center gap-3 rounded-xl border border-dashed bg-card/60 px-3 py-2.5 text-left transition-[border-color] outline-none hover:border-ring/40 focus-visible:ring-2 focus-visible:ring-ring"
                data-slot="floor-outside"
                @click="showOutside"
              >
                <span
                  class="flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground"
                  aria-hidden="true"
                >
                  <MapPinned class="size-4" />
                </span>
                <span class="grid min-w-0 flex-1">
                  <span class="truncate text-sm font-medium">{{ t('floors.view.outside') }}</span>
                  <span class="truncate text-xs text-muted-foreground">
                    {{ t('floors.view.deviceCount', outside.length) }}
                  </span>
                </span>
                <ChevronRight
                  class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                  aria-hidden="true"
                />
              </button>
            </li>
          </ul>

          <!-- The traced plan image, faintly under the rooms -->
          <div
            v-if="floor?.background"
            class="mt-2 flex items-center justify-between gap-3 rounded-xl border bg-card px-3 py-2.5"
          >
            <Label :for="originalId" class="text-sm font-normal">{{ t('floors.view.showOriginal') }}</Label>
            <Switch :id="originalId" v-model="showOriginal" />
          </div>

          <p
            v-if="session.isAdmin && floor && markers.length === 0 && floor.rooms.length > 0"
            class="mt-2 rounded-xl border border-dashed bg-card/60 px-3 py-2.5 text-sm text-muted-foreground"
          >
            {{ t('floors.view.noDevicesAdmin') }}
            <RouterLink
              :to="{ name: 'floor-edit', params: { id: floor.id }, query: { tab: 'devices' } }"
              class="font-medium text-primary underline-offset-4 hover:underline"
            >
              {{ t('floors.view.placeDevices') }}
            </RouterLink>
          </p>
        </section>
      </div>
    </template>

    <!-- A device's controls: beside it on the plan, or a sheet on phones -->
    <Popover v-if="!isPhone" :open="activeMarker !== null" @update:open="onPanelOpenChange">
      <PopoverContent
        v-if="activeMarker"
        :reference="anchor ?? undefined"
        :aria-label="activeMarker.name"
        side="right"
        align="start"
        :side-offset="12"
        :collision-padding="16"
        class="max-h-[min(78dvh,40rem)] w-[23rem] overflow-y-auto rounded-2xl p-4 shadow-lg"
      >
        <DeviceQuickPanel
          :device="activeMarker.device"
          :endpoints="activeMarker.endpoints"
          :name="activeMarker.name"
          :room-name="activeRoom"
        />
      </PopoverContent>
    </Popover>
    <Sheet v-else :open="activeMarker !== null" @update:open="onPanelOpenChange">
      <SheetContent
        side="bottom"
        :close-label="t('common.close')"
        class="max-h-[85dvh] gap-0 overflow-y-auto rounded-t-2xl p-4 pt-10"
      >
        <SheetHeader class="sr-only">
          <SheetTitle>{{ activeMarker?.name }}</SheetTitle>
          <SheetDescription>{{ t('floorplan.devices.panelDescription') }}</SheetDescription>
        </SheetHeader>
        <DeviceQuickPanel
          v-if="activeMarker"
          :device="activeMarker.device"
          :endpoints="activeMarker.endpoints"
          :name="activeMarker.name"
          :room-name="activeRoom"
        />
      </SheetContent>
    </Sheet>
  </div>
</template>
