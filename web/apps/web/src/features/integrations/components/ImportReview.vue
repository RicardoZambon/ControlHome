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
import { Sparkles } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, shallowRef } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { initialChoice, reviewChanges, suggestRoom, type ReviewChoice } from '../importReview'

/**
 * After a new integration: the devices it brought (they arrive a moment after it starts), each kept or hidden, and the
 * room it goes to — suggested from its name. "Finish" saves only what changed; "Skip" keeps everything as it came.
 */
const props = defineProps<{ integrationId: string }>()
const emit = defineEmits<{ finish: [] }>()

const { t } = useI18n()
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
const suggestedIds = computed(
  () =>
    new Set(
      devices.value
        .filter((device) => device.roomId === null && suggestRoom(device.name, rooms.value) !== null)
        .map((device) => device.id),
    ),
)

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

    <ul v-else class="grid gap-2" data-slot="review-list">
      <li
        v-for="device in devices"
        :key="device.id"
        :class="
          cn(
            'grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-3 gap-y-3 rounded-xl border bg-card p-3 transition-opacity sm:grid-cols-[auto_minmax(0,1fr)_14rem]',
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
            <span class="truncate text-xs text-muted-foreground">
              {{
                choices.get(device.id)?.keep ? t('integrations.review.kept') : t('integrations.review.hidden')
              }}
            </span>
          </span>
        </label>
        <div class="col-span-2 flex items-center gap-2 sm:col-span-1">
          <Select
            :model-value="roomValue(device.id)"
            :disabled="!choices.get(device.id)?.keep"
            @update:model-value="(value) => setRoom(device.id, value)"
          >
            <SelectTrigger
              class="w-full"
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
          <Sparkles
            v-if="
              suggestedIds.has(device.id) &&
              choices.get(device.id)?.roomId === suggestRoom(device.name, rooms)
            "
            class="size-4 shrink-0 text-primary"
            :aria-label="t('integrations.review.suggested')"
            role="img"
          />
        </div>
      </li>
    </ul>

    <p v-if="devices.length > 0 && rooms.length === 0" class="text-sm text-muted-foreground">
      {{ t('integrations.review.noRooms') }}
    </p>

    <div class="flex flex-wrap items-center justify-end gap-2 border-t pt-5">
      <p v-if="waiting && devices.length > 0" class="mr-auto text-sm text-muted-foreground" role="status">
        {{ t('integrations.review.stillArriving') }}
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
