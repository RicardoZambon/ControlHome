<script setup lang="ts">
import { getFloor, listFloors, type FloorDto, type FloorSummaryDto } from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Skeleton, cn } from '@controlhome/ui'
import { ArrowLeft, PencilRuler, RefreshCw } from '@lucide/vue'
import { useMediaQuery } from '@vueuse/core'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import PageHeader from '../../../components/PageHeader.vue'
import FloorPlanCanvas from '../../../components/floorplan/FloorPlanCanvas.vue'
import { floorEyebrow, sortFloors, totalAreaM2 } from '../../../components/floorplan/floors'
import { formatArea, roomAreaM2 } from '../../../components/floorplan/measure'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'

/** A floor in view mode: the plan (pan, zoom, tap a room), its rooms, the other floors, and (Admin) the editor. */
const { t, locale } = useI18n()
const route = useRoute()
const session = useSessionStore()
const problem = useProblem()

const floorId = computed(() => Number(route.params.id))
const floor = ref<FloorDto | null>(null)
const floors = ref<FloorSummaryDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)
const selectedRoomId = ref<number | null>(null)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  selectedRoomId.value = null
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

const rooms = computed(() =>
  (floor.value?.rooms ?? []).map((room) => ({
    room,
    area: formatArea(roomAreaM2(room.polygon), locale.value),
    Icon: roomIcon(room.icon),
    colors: roomColorClasses(room.colorToken),
  })),
)
const summary = computed(() => {
  if (!floor.value) return ''
  const count = t('floors.roomCount', floor.value.rooms.length)
  return floor.value.rooms.length > 0
    ? `${count} · ${formatArea(totalAreaM2(floor.value), locale.value)}`
    : count
})

const isLarge = useMediaQuery('(min-width: 1024px)')
/** The plan box follows the floor's proportions on narrow screens (within reason). */
const planAspect = computed(() => {
  const ratio = floor.value ? floor.value.widthCm / Math.max(floor.value.heightCm, 1) : 4 / 3
  // A little taller than the floor itself: the dimension lines need room around it.
  return Math.min(1.5, Math.max(0.8, ratio)) * 0.78
})

function toggleRoom(id: number): void {
  selectedRoomId.value = selectedRoomId.value === id ? null : id
}
</script>

<template>
  <div>
    <RouterLink
      :to="{ name: 'floors' }"
      class="mb-4 inline-flex items-center gap-1.5 rounded-sm text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
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
        :eyebrow="floor ? floorEyebrow(floor, t) : undefined"
        :title="floor?.name ?? t('floors.view.title')"
        :description="summary || undefined"
        class="pb-6 md:pb-8"
      >
        <template v-if="session.isAdmin && floor" #actions>
          <Button as-child class="max-sm:w-full">
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
          <span class="text-xs">{{ floorEyebrow(entry, t) }}</span>
        </RouterLink>
      </nav>

      <div class="grid grid-cols-[minmax(0,1fr)] gap-6 lg:grid-cols-[minmax(0,1fr)_18rem]">
        <!-- Phones and tablets: as tall as the floor needs at full width; desktops: most of the screen. -->
        <div
          class="relative w-full overflow-hidden rounded-2xl border shadow-sm lg:h-[min(68dvh,46rem)] lg:min-h-[22rem]"
          :style="isLarge ? undefined : { aspectRatio: planAspect }"
        >
          <Skeleton v-if="loading" class="size-full rounded-none" />
          <FloorPlanCanvas
            v-else-if="floor"
            class="size-full"
            :floor="floor"
            :selected-room-id="selectedRoomId"
            @room-click="(room) => room.id && toggleRoom(room.id)"
            @background-click="selectedRoomId = null"
          >
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
                <span class="min-w-0 flex-1 truncate text-sm font-medium">{{ entry.room.name }}</span>
                <span class="text-sm text-muted-foreground tabular-nums">{{ entry.area }}</span>
              </button>
            </li>
          </ul>
        </section>
      </div>
    </template>
  </div>
</template>
