<script setup lang="ts">
import { listDevices, updateDevice, updateEndpoint, type DeviceSummaryDto } from '@controlhome/api-client'
import {
  Button,
  Checkbox,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Skeleton,
  cn,
  toast,
} from '@controlhome/ui'
import { LoaderCircle, Sparkles } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, shallowRef } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import { deviceKindLabel } from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { initialChoice, reviewChanges, roomSuggestion, type ReviewChoice } from '../importReview'

/**
 * After a new integration: the devices it brought (they arrive a moment after it starts), each kept or hidden, and the
 * room it goes to — the one its vendor app suggested, else one suggested from its name (marked as such until changed).
 * "Finish" saves only what changed; "Skip" keeps everything as it came.
 */
const props = defineProps<{ integrationId: string }>()
const emit = defineEmits<{ finish: [] }>()

const { t, te } = useI18n()
const problem = useProblem()
const store = useDevicesStore()

const NO_ROOM = 'none'
/** How long (and how often) the review waits for the integration's devices to arrive. */
const WAIT_MS = 20_000
const POLL_MS = 1_500

const devices = shallowRef<DeviceSummaryDto[]>([])
const choices = reactive(new Map<string, ReviewChoice>())
const waiting = ref(true)
const saving = ref(false)
const error = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
let startedAt = 0
let stableRounds = 0

const rooms = computed(() => store.rooms)
const multipleFloors = computed(() => new Set(rooms.value.map((room) => room.floorId)).size > 1)

async function poll(): Promise<void> {
  try {
    const list = unwrap(await listDevices({ query: { integrationId: props.integrationId } }))
      .filter((device) => !device.isOrphaned)
      .sort((a, b) => a.name.localeCompare(b.name))
    stableRounds = list.length > 0 && list.length === devices.value.length ? stableRounds + 1 : 0
    for (const device of list)
      if (!choices.has(device.id)) choices.set(device.id, initialChoice(device, rooms.value))
    devices.value = list
  } catch {
    // The next round tries again.
  }
  // Done once the list stopped growing (two rounds), or after WAIT_MS.
  if (stableRounds >= 2 || Date.now() - startedAt > WAIT_MS) {
    waiting.value = false
    return
  }
  timer = setTimeout(() => void poll(), POLL_MS)
}

onMounted(async () => {
  startedAt = Date.now()
  await store.load()
  void poll()
})
onBeforeUnmount(() => clearTimeout(timer))

const keptCount = computed(() => [...choices.values()].filter((choice) => choice.keep).length)
const suggestions = computed(
  () => new Map(devices.value.map((device) => [device.id, roomSuggestion(device, rooms.value)])),
)
const anySuggested = computed(() => devices.value.some((device) => suggestionLabel(device.id)))

/** Keep every device, or none ("indeterminate" while some are). */
const allKept = computed<boolean | 'indeterminate'>(() => {
  if (keptCount.value === 0) return false
  return keptCount.value === devices.value.length ? true : 'indeterminate'
})

function setAll(keep: boolean | 'indeterminate'): void {
  for (const device of devices.value) setKeep(device.id, keep === true)
}

/** What a device is ("Light", "Outlet"…). */
const kindOf = (device: DeviceSummaryDto) => deviceKindLabel(device, t, te)

/** What the mark beside a room says, while the room chosen is still the one suggested. */
function suggestionLabel(id: string): string | null {
  const suggestion = suggestions.value.get(id)
  if (!suggestion || choices.get(id)?.roomId !== suggestion.roomId) return null
  return suggestion.source === 'integration'
    ? t('integrations.review.suggestedByIntegration')
    : t('integrations.review.suggested')
}

function setKeep(id: string, keep: boolean | 'indeterminate'): void {
  const current = choices.get(id)
  if (current) choices.set(id, { ...current, keep: keep === true })
}

function setRoom(id: string, value: unknown): void {
  const current = choices.get(id)
  if (current) choices.set(id, { ...current, roomId: value === NO_ROOM ? null : Number(value) })
}

function roomValue(id: string): string {
  const roomId = choices.get(id)?.roomId ?? null
  return roomId === null ? NO_ROOM : String(roomId)
}

async function finish(): Promise<void> {
  clearTimeout(timer)
  error.value = ''
  saving.value = true
  try {
    const changes = reviewChanges(devices.value, choices)
    for (const change of changes) {
      if (change.kind === 'room') {
        unwrap(
          await updateDevice({
            path: { id: change.device.id },
            body: { name: change.device.name, icon: change.device.icon, roomId: change.roomId },
          }),
        )
      } else {
        for (const endpoint of change.device.endpoints.filter((entry) => !entry.hidden)) {
          unwrap(
            await updateEndpoint({
              path: { id: endpoint.id },
              body: { name: endpoint.name, icon: endpoint.icon, roomId: endpoint.roomId, hidden: true },
            }),
          )
        }
      }
    }
    if (changes.length > 0) void store.load(true)
    toast.success(t('integrations.review.saved', { count: keptCount.value }, keptCount.value))
    emit('finish')
  } catch (failure) {
    error.value = problem.message(failure)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="grid gap-6" data-slot="import-review" aria-labelledby="import-review-title">
    <header class="grid gap-2">
      <p class="eyebrow">{{ t('integrations.review.eyebrow') }}</p>
      <h2
        id="import-review-title"
        class="font-serif text-2xl leading-tight font-medium tracking-tight sm:text-3xl"
      >
        {{
          devices.length > 0
            ? t('integrations.review.title', { count: devices.length }, devices.length)
            : t('integrations.review.waitingTitle')
        }}
      </h2>
      <p class="max-w-prose text-pretty text-muted-foreground">{{ t('integrations.review.description') }}</p>
    </header>

    <FormAlert v-if="error" :message="error" />

    <div
      v-if="devices.length === 0 && waiting"
      class="grid gap-2"
      role="status"
      :aria-label="t('integrations.review.waiting')"
    >
      <Skeleton v-for="index in 3" :key="index" class="h-16 w-full rounded-xl" />
      <p class="text-sm text-muted-foreground">{{ t('integrations.review.waiting') }}</p>
    </div>
    <p
      v-else-if="devices.length === 0"
      class="rounded-xl border border-dashed p-5 text-sm text-muted-foreground"
    >
      {{ t('integrations.review.none') }}
    </p>

    <div v-else class="grid gap-2">
      <!-- Every device at once, and how many are kept -->
      <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-3 text-sm">
        <label class="flex cursor-pointer items-center gap-3 font-medium">
          <Checkbox :model-value="allKept" data-slot="review-all" @update:model-value="setAll" />
          {{ t('integrations.review.keepAll') }}
        </label>
        <span class="text-muted-foreground tabular-nums">
          {{ t('integrations.review.keptCount', { kept: keptCount, total: devices.length }) }}
        </span>
      </div>
      <ul class="grid gap-2" data-slot="review-list">
        <li
          v-for="device in devices"
          :key="device.id"
          :class="
            cn(
              'grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-3 gap-y-3 rounded-xl border bg-card p-3 transition-opacity sm:grid-cols-[auto_minmax(0,1fr)_16rem]',
              !choices.get(device.id)?.keep && 'bg-muted/40 opacity-70',
            )
          "
          :data-device-id="device.id"
        >
          <Checkbox
            :id="`review-${device.id}`"
            :model-value="choices.get(device.id)?.keep ?? true"
            :aria-label="t('integrations.review.keep', { name: device.name })"
            @update:model-value="(value) => setKeep(device.id, value)"
          />
          <label :for="`review-${device.id}`" class="flex min-w-0 cursor-pointer items-center gap-3">
            <DeviceIcon
              size="sm"
              :icon="device.icon"
              :device-class="device.deviceClass"
              :capabilities="
                device.endpoints.flatMap((endpoint) => endpoint.capabilities.map((c) => c.capabilityId))
              "
            />
            <span class="grid min-w-0">
              <span class="truncate text-sm font-medium">{{ device.name }}</span>
              <span class="truncate text-xs text-muted-foreground" data-slot="review-kind">
                {{ choices.get(device.id)?.keep ? kindOf(device) : t('integrations.review.hidden') }}
              </span>
            </span>
          </label>
          <div class="col-span-2 flex items-center gap-2 sm:col-span-1">
            <Select
              :model-value="roomValue(device.id)"
              :disabled="!choices.get(device.id)?.keep"
              @update:model-value="(value) => setRoom(device.id, value)"
            >
              <!-- min-w-0: a long room name ("Living room · Ground floor") is cut short instead of pushing the mark out -->
              <SelectTrigger
                class="w-full min-w-0"
                :aria-label="t('integrations.review.room', { name: device.name })"
                data-slot="review-room"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem :value="NO_ROOM">{{ t('device.noRoom') }}</SelectItem>
                <SelectItem v-for="room in rooms" :key="room.id" :value="String(room.id)">
                  {{ multipleFloors ? `${room.name} · ${room.floorName}` : room.name }}
                </SelectItem>
              </SelectContent>
            </Select>
            <!-- The room was suggested (by the integration, or from the name): a mark until it is changed -->
            <span
              class="flex size-4 shrink-0 text-primary"
              :role="suggestionLabel(device.id) ? 'img' : undefined"
              :aria-label="suggestionLabel(device.id) ?? undefined"
              :title="suggestionLabel(device.id) ?? undefined"
              data-slot="review-suggested"
            >
              <Sparkles v-if="suggestionLabel(device.id)" class="size-4" aria-hidden="true" />
            </span>
          </div>
        </li>
      </ul>
      <!-- What the mark beside a room means -->
      <p v-if="anySuggested" class="flex items-center gap-2 px-3 text-xs text-muted-foreground">
        <Sparkles class="size-3.5 shrink-0 text-primary" aria-hidden="true" />
        {{ t('integrations.review.legend') }}
      </p>
    </div>

    <p v-if="devices.length > 0 && rooms.length === 0" class="text-sm text-muted-foreground">
      {{ t('integrations.review.noRooms') }}
    </p>

    <!-- On phones the actions stay in reach above the tab bar while the list scrolls. -->
    <div
      class="z-10 flex flex-wrap items-center justify-end gap-2 border-t bg-card pt-5 max-md:sticky max-md:bottom-[calc(3.5rem+env(safe-area-inset-bottom))] max-md:-mx-6 max-md:px-6 max-md:pb-4 sm:max-md:-mx-8 sm:max-md:px-8"
      data-slot="review-actions"
    >
      <p
        v-if="waiting && devices.length > 0"
        class="mr-auto flex items-center gap-2 text-sm text-muted-foreground"
        role="status"
      >
        <LoaderCircle class="size-4 shrink-0 motion-safe:animate-spin" aria-hidden="true" />
        {{ t('integrations.review.stillArriving', { count: devices.length }, devices.length) }}
      </p>
      <Button variant="ghost" :disabled="saving" @click="emit('finish')">{{
        t('integrations.review.skip')
      }}</Button>
      <Button :loading="saving" :disabled="devices.length === 0" data-slot="review-finish" @click="finish">
        {{ t('integrations.review.finish') }}
      </Button>
    </div>
  </section>
</template>
