<script setup lang="ts">
import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { cn } from '@controlhome/ui'
import { EyeOff, Star } from '@lucide/vue'
import { computed, useId, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { asNumber } from '../../lib/stateValue'
import { endpointName, useDevicesStore } from '../../stores/devices'
import { useRealtimeStore } from '../../stores/realtime'
import { useSessionStore } from '../../stores/session'
import DeviceIcon from './DeviceIcon.vue'
import { CapabilityControl, primaryControl } from './capabilities'
import { summarizeEndpoint } from './deviceState'
import { lightColorOf, lightLayers } from './lightColor'
import { TONES } from './tone'

/**
 * One endpoint as a tile: its icon, name and room, the state in a word with its state color, and the quick action
 * (the primary capability's control: power, lock, cover, …) for Members and Admins. The whole tile opens the device
 * page; the quick action sits above that link. A tile that is on glows softly — in the light's own color for color
 * bulbs; an offline tile gets a dashed outline.
 */
const props = defineProps<{
  device: DeviceSummaryDto
  endpoint: EndpointDto
  /** The room's name, if the endpoint is in one. */
  roomName?: string
  class?: HTMLAttributes['class']
}>()

const { t, te, locale } = useI18n()
const realtime = useRealtimeStore()
const devices = useDevicesStore()
const session = useSessionStore()
const nameId = useId()

const ids = computed(() => {
  const map = new Map<string, number>()
  for (const capability of props.endpoint.capabilities)
    for (const attribute of capability.attributes)
      map.set(`${capability.capabilityId}.${attribute.key}`, attribute.attributeId)
  return map
})
const read = (capabilityId: string, key: string) => realtime.valueOf(ids.value.get(`${capabilityId}.${key}`))

const availability = computed(() => devices.availabilityOf(props.device))
const summary = computed(() =>
  summarizeEndpoint(props.endpoint, read, {
    availability: availability.value,
    deviceClass: props.device.deviceClass,
    locale: locale.value,
    t,
    te,
  }),
)
const capabilityIds = computed(() =>
  props.endpoint.capabilities
    .filter((capability) => !capability.retired)
    .map((capability) => capability.capabilityId),
)
const name = computed(() => endpointName(props.device, props.endpoint))
const pending = computed(() => realtime.isPending(props.endpoint.id))

/** The quick action: Members and Admins only (Viewers see the state). */
const quick = computed(() => {
  if (!session.hasRole('Admin', 'Member')) return undefined
  const control = primaryControl(capabilityIds.value)
  if (!control) return undefined
  const capability = props.endpoint.capabilities.find(
    (entry) => entry.capabilityId === control.capabilityId && !entry.retired,
  )
  return capability ? { capability } : undefined
})

/** A lit color bulb glows in its own color. */
const glow = computed(() => {
  if (summary.value.tone === 'on' && capabilityIds.value.includes('color')) {
    const color = lightColorOf({
      hue: asNumber(read('color', 'hue')),
      saturation: asNumber(read('color', 'saturation')),
      colorTemperature: asNumber(read('color', 'colorTemperature')),
    })
    if (color) return lightLayers(color).glow
  }
  return TONES[summary.value.tone].glow
})
const iconTone = computed(() =>
  summary.value.tone === 'off' || summary.value.tone === 'neutral' ? 'neutral' : summary.value.tone,
)
</script>

<template>
  <article
    :aria-labelledby="nameId"
    data-slot="device-tile"
    :data-endpoint-id="endpoint.id"
    :data-tone="summary.tone"
    :class="
      cn(
        'group relative isolate flex min-h-40 flex-col justify-between gap-5 overflow-hidden rounded-2xl border bg-card p-4 shadow-xs transition-[border-color,box-shadow] duration-300 has-[a:focus-visible]:ring-2 has-[a:focus-visible]:ring-ring has-[a:focus-visible]:ring-offset-2 has-[a:focus-visible]:ring-offset-background hover:shadow-md',
        summary.active ? TONES[summary.tone].border : 'hover:border-ring/40',
        summary.tone === 'offline' && TONES.offline.border,
        summary.tone === 'danger' && TONES.danger.border,
        props.class,
      )
    "
  >
    <div
      aria-hidden="true"
      :class="
        cn(
          'pointer-events-none absolute inset-0 -z-10 bg-radial-[at_0%_0%] via-transparent via-60% to-transparent transition-opacity duration-500',
          glow,
          summary.active ? 'opacity-100' : 'opacity-0',
        )
      "
    />

    <div class="flex items-start justify-between gap-3">
      <DeviceIcon
        :icon="endpoint.icon ?? device.icon"
        :device-class="device.deviceClass"
        :capabilities="capabilityIds"
        :tone="iconTone"
      />
      <div v-if="quick" class="relative z-10 -mt-0.5 -mr-0.5">
        <CapabilityControl
          :device="device"
          :endpoint="endpoint"
          :capability="quick.capability"
          variant="tile"
        />
      </div>
    </div>

    <div class="grid min-w-0 gap-1">
      <h3 :id="nameId" class="flex min-w-0 items-center gap-1.5 leading-snug font-medium">
        <RouterLink
          :to="{ name: 'device', params: { id: device.id } }"
          class="line-clamp-2 break-words outline-none after:absolute after:inset-0 after:content-['']"
        >
          {{ name }}
        </RouterLink>
        <Star
          v-if="endpoint.favorite"
          class="size-3.5 shrink-0 fill-state-energy text-state-energy"
          :aria-label="t('device.favorite')"
          role="img"
        />
        <EyeOff
          v-if="endpoint.hidden"
          class="size-3.5 shrink-0 text-muted-foreground"
          :aria-label="t('device.hidden')"
          role="img"
        />
      </h3>
      <p class="truncate text-xs text-muted-foreground">{{ roomName ?? t('device.noRoom') }}</p>
      <p class="flex min-w-0 items-center gap-1.5 text-sm" data-slot="tile-state">
        <span
          aria-hidden="true"
          :class="cn('inline-block size-2 shrink-0 rounded-full', TONES[summary.tone].dot)"
        />
        <span class="truncate font-medium">{{ pending ? t('device.state.pending') : summary.label }}</span>
        <span
          v-if="summary.details.length > 0 && !pending"
          class="truncate text-muted-foreground tabular-nums"
        >
          · {{ summary.details.join(' · ') }}
        </span>
      </p>
    </div>
  </article>
</template>
