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
import { ArrowLeft, PencilRuler, RefreshCw } from '@lucide/vue'
import { useMediaQuery } from '@vueuse/core'
import { computed, onMounted, ref, shallowRef, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import PageHeader from '../../../components/PageHeader.vue'
import DeviceQuickPanel from '../../../components/devices/DeviceQuickPanel.vue'
import FloorPlanCanvas, { type CanvasPointerHandler } from '../../../components/floorplan/FloorPlanCanvas.vue'
import PlacementLayer from '../../../components/floorplan/PlacementLayer.vue'
import { floorEyebrow, sortFloors, totalAreaM2 } from '../../../components/floorplan/floors'
import { MARKER_HIT_SLOP_PX, MARKER_RADIUS_PX } from '../../../components/floorplan/markerTone'
import { formatArea, roomAreaM2 } from '../../../components/floorplan/measure'
import { hitMarker, planMarkers, type PlanMarker } from '../../../components/floorplan/placements'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
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
const devicesByRoom = computed(() => {
  const counts = new Map<number, number>()
  for (const marker of markers.value) {
    const roomId = marker.placement.roomId
    if (roomId !== null) counts.set(roomId, (counts.get(roomId) ?? 0) + 1)
  }
  return counts
})

const canvas = ref<InstanceType<typeof FloorPlanCanvas> | null>(null)
const planBox = ref<HTMLElement | null>(null)
const coarse = useMediaQuery('(pointer: coarse)')

function open(marker: PlanMarker): void {
  anchor.value = planBox.value?.querySelector(`[data-placement-id="${marker.placement.id}"]`) ?? null
  active.value = marker
}

/** A tap on a device opens its controls (a tap elsewhere goes on to the rooms). */
const handler: CanvasPointerHandler = {
  tap(pointer) {
    const scale = canvas.value?.scale ?? 1
    const radius = (coarse.value ? MARKER_RADIUS_PX.coarse : MARKER_RADIUS_PX.fine) + MARKER_HIT_SLOP_PX
    const hit = hitMarker(markers.value, pointer.cm, radius / scale)
    if (!hit) return false
    open(hit)
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
const rooms = computed(() =>
  (floor.value?.rooms ?? []).map((room) => ({
    room,
    area: formatArea(roomAreaM2(room.polygon), locale.value),
    devices: devicesByRoom.value.get(room.id) ?? 0,
    Icon: roomIcon(room.icon),
    colors: roomColorClasses(room.colorToken),
  })),
)
const summary = computed(() => {
  if (!floor.value) return ''
  const parts = [t('floors.roomCount', floor.value.rooms.length)]
  if (floor.value.rooms.length > 0) parts.push(formatArea(totalAreaM2(floor.value), locale.value))
  if (markers.value.length > 0) parts.push(t('floors.view.deviceCount', markers.value.length))
  return parts.join(' · ')
})

usePageTrail(() =>
  floor.value ? [{ label: t('floors.title'), to: { name: 'floors' } }, { label: floor.value.name }] : null,
)

const isLarge = useMediaQuery('(min-width: 1024px)')
const isPhone = useMediaQuery('(max-width: 639px)')
/** The plan box follows the floor's proportions on narrow screens (within reason). */
const planAspect = computed(() => {
  const ratio = floor.value ? floor.value.widthCm / Math.max(floor.value.heightCm, 1) : 4 / 3
  // A touch taller than the floor itself, for the padding around the sheet (phones show no dimension lines).
  return Math.min(1.5, Math.max(0.8, ratio)) * 0.92
})

const originalId = useId()
const showOriginal = ref(false)
watch(floorId, () => (showOriginal.value = false))

function toggleRoom(id: number): void {
  selectedRoomId.value = selectedRoomId.value === id ? null : id
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
      <PageHeader
        :eyebrow="(floor && floorEyebrow(floor, t)) || undefined"
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
            :background-image="showOriginal ? 'shown' : 'auto'"
            :selected-room-id="selectedRoomId"
            :handler="handler"
            @room-click="(room) => room.id && toggleRoom(room.id)"
            @background-click="selectedRoomId = null"
          >
            <template #overlay="{ scale }">
              <PlacementLayer
                :markers="markers"
                :scale="scale"
                :coarse="coarse"
                :selected-id="activeMarker?.placement.id ?? null"
                @activate="open"
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
          <ul v-else class="grid gap-1.5">
            <li v-for="entry in rooms" :key="entry.room.id">
              <button
                type="button"
                :aria-pressed="selectedRoomId === entry.room.id"
                :class="
                  cn(
                    'flex w-full items-center gap-3 rounded-xl border bg-card px-3 py-2.5 text-left transition-[border-color,background-color] outline-none hover:border-ring/40 focus-visible:ring-2 focus-visible:ring-ring',
                    selectedRoomId === entry.room.id && 'border-ring/60 bg-accent/50',
                  )
                "
                @click="toggleRoom(entry.room.id)"
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
                  <span v-if="entry.devices > 0" class="text-xs text-muted-foreground">
                    {{ t('floors.view.deviceCount', entry.devices) }}
                  </span>
                </span>
                <span class="text-sm text-muted-foreground tabular-nums">{{ entry.area }}</span>
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
