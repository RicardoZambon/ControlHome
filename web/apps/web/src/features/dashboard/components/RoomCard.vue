<script setup lang="ts">
import type { DashboardRoomDto } from '@controlhome/api-client'
import { cn } from '@controlhome/ui'
import { Thermometer, WifiOff, Zap } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatMeasurement } from '../../../components/devices/format'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import { formatPower, type RoomLive } from '../dashboardLive'

/**
 * A room at a glance: its color and icon, how many of its devices are on (a bar of that share), its temperature and
 * the power it draws. Opens the device grid filtered to the room.
 */
const props = defineProps<{
  room: DashboardRoomDto
  live: RoomLive
  /** Show the floor's name (the home has several). */
  showFloor?: boolean
  /** How many of its devices are offline (a chip says so). */
  offline?: number
}>()

const { t, locale } = useI18n()

const colors = computed(() => roomColorClasses(props.room.colorToken))
const Icon = computed(() => roomIcon(props.room.icon))
const share = computed(() =>
  props.room.deviceCount > 0 ? Math.round((props.live.activeDeviceCount / props.room.deviceCount) * 100) : 0,
)
const temperature = computed(() =>
  props.live.temperatureC === null ? null : formatMeasurement(props.live.temperatureC, '°C', locale.value),
)
const power = computed(() =>
  props.live.powerW === null ? null : formatPower(props.live.powerW, locale.value),
)
</script>

<template>
  <RouterLink
    :to="{ name: 'devices', query: { room: room.id } }"
    class="group flex h-full flex-col gap-4 rounded-2xl border bg-card p-4 shadow-xs transition-[border-color,box-shadow] outline-none hover:border-ring/40 hover:shadow-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
    data-slot="room-card"
    :data-room-id="room.id"
  >
    <div class="flex items-start gap-3">
      <span class="shrink-0 rounded-xl bg-card" aria-hidden="true">
        <span :class="cn('flex size-10 items-center justify-center rounded-xl border', colors.sample)">
          <component :is="Icon" class="size-5" />
        </span>
      </span>
      <span class="grid min-w-0 flex-1 gap-0.5">
        <span v-if="showFloor" class="eyebrow truncate">{{ room.floorName }}</span>
        <span class="truncate font-serif text-lg leading-snug font-medium tracking-tight">{{
          room.name
        }}</span>
      </span>
      <span
        v-if="offline"
        class="inline-flex shrink-0 items-center gap-1 rounded-full border border-dashed border-state-offline/70 px-2 py-0.5 text-xs font-medium text-state-offline"
        data-slot="room-offline"
      >
        <WifiOff class="size-3" aria-hidden="true" />
        {{ t('dashboard.rooms.offline', { n: offline }) }}
      </span>
    </div>

    <div class="grid gap-1.5">
      <p class="flex items-baseline justify-between gap-2 text-sm">
        <span :class="live.activeDeviceCount > 0 ? 'font-medium' : 'text-muted-foreground'">
          {{
            room.deviceCount === 0
              ? t('dashboard.rooms.noDevices')
              : t(
                  'dashboard.rooms.active',
                  { active: live.activeDeviceCount, total: room.deviceCount },
                  room.deviceCount,
                )
          }}
        </span>
      </p>
      <span
        v-if="room.deviceCount > 0"
        class="block h-1 overflow-hidden rounded-full bg-muted"
        aria-hidden="true"
      >
        <span
          :class="
            cn(
              'block h-full rounded-full transition-[width] duration-500',
              live.activeDeviceCount > 0 ? 'bg-state-on' : 'bg-transparent',
            )
          "
          :style="{ width: `${share}%` }"
        />
      </span>
    </div>

    <div
      class="mt-auto flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground tabular-nums"
    >
      <span class="inline-flex items-center gap-1.5" data-slot="room-temperature">
        <Thermometer class="size-3.5" aria-hidden="true" />
        <span class="sr-only">{{ t('dashboard.rooms.temperature') }}:</span>
        <span :class="temperature && 'text-foreground'">{{ temperature ?? '—' }}</span>
      </span>
      <span class="inline-flex items-center gap-1.5" data-slot="room-power">
        <Zap class="size-3.5" aria-hidden="true" />
        <span class="sr-only">{{ t('dashboard.rooms.power') }}:</span>
        <span :class="power && 'text-foreground'">{{ power ? `${power.value} ${power.unit}` : '—' }}</span>
      </span>
    </div>
  </RouterLink>
</template>
