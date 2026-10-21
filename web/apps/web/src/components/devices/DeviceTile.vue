<script setup lang="ts">
import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { cn } from '@controlhome/ui'
import { EyeOff, Star, Unlink, WifiOff } from '@lucide/vue'

import { computed, useId, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRelative } from '../../lib/format'
import { useServerNow } from '../../lib/serverClock'
import { asNumber } from '../../lib/stateValue'
import { endpointName, useDevicesStore } from '../../stores/devices'
import { useRealtimeStore } from '../../stores/realtime'
import { useSessionStore } from '../../stores/session'
import DeviceIcon from './DeviceIcon.vue'
import { CapabilityControl, primaryControl } from './capabilities'
import { standsOpen, summarizeEndpoint } from './deviceState'
import { KELVIN_STOPS, isLightEndpoint, lightColorOf, lightLayers } from './lightColor'
import { pendingLabel } from './pendingLabel'
import { TONES, iconToneOf } from './tone'

/**
 * One endpoint as a tile: its icon, name (and room, outside the room groups), the state in a word with its state color,
 * and the quick action (the primary capability's control: power, lock, cover, …) for Members and Admins. The whole
 * tile opens the device page; the quick action sits above that link.
 *
 * - On: a soft glow (a lit light glows warm, a color bulb in its own color; dimmers show their level as a bar along
 *   the bottom edge).
 * - Needs attention (unlocked, open, alarm): the warning or danger border and glow.
 * - Offline: dashed outline, muted icon and name, and "Offline · 12 minutes ago" in place of the quick action.
 * - No longer reported by its integration (orphaned): a broken-link mark beside the name.
 * - A command on its way: the state says what is happening ("Turning on…", "Locked · Waiting for your PIN").
 */
const props = withDefaults(
  defineProps<{
    device: DeviceSummaryDto
    endpoint: EndpointDto
    /** The room's name, if the endpoint is in one. */
    roomName?: string
    /** Show the room under the name (off inside a room's own group, which already names it). */
    showRoom?: boolean
    class?: HTMLAttributes['class']
  }>(),
  { showRoom: true },
)

const { t, te, locale } = useI18n()
const realtime = useRealtimeStore()
const devices = useDevicesStore()
const session = useSessionStore()
const nameId = useId()
const now = useServerNow(30_000)

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
const offline = computed(() => summary.value.tone === 'offline')
const open = computed(() => !offline.value && standsOpen(capabilityIds.value, read))
const pending = computed(() => realtime.pendingFor(props.endpoint.id))

/** The state line: what a command is doing while it is on its way, else the state and its details. */
const stateLine = computed(() => {
  const command = pending.value
  if (command?.awaitingPin)
    return { label: summary.value.label, details: [pendingLabel(command, t, te, locale.value)] }
  if (command) return { label: pendingLabel(command, t, te, locale.value), details: [] }
  const details = [...summary.value.details]
  if (offline.value && props.device.lastSeenAtUtc)
    details.push(formatRelative(props.device.lastSeenAtUtc, now.value, locale.value, t('common.justNow')))
  return { label: summary.value.label, details }
})

/** The quick action: Members and Admins only (Viewers see the state); none while the device is offline. */
const quick = computed(() => {
  if (!session.hasRole('Admin', 'Member') || offline.value) return undefined
  const control = primaryControl(capabilityIds.value)
  if (!control) return undefined
  const capability = props.endpoint.capabilities.find(
    (entry) => entry.capabilityId === control.capabilityId && !entry.retired,
  )
  return capability ? { capability } : undefined
})

/** A light (dimmable, colored or classed as one): lit, it glows warm or in its own color. */
const lit = computed(
  () => summary.value.tone === 'on' && isLightEndpoint(capabilityIds.value, props.device.deviceClass),
)
const lightColor = computed(() =>
  lit.value && capabilityIds.value.includes('color')
    ? lightColorOf({
        hue: asNumber(read('color', 'hue')),
        saturation: asNumber(read('color', 'saturation')),
        colorTemperature: asNumber(read('color', 'colorTemperature')),
      })
    : undefined,
)
/** Something to look at: on, running, or needing attention (unlocked, open, an alarm). */
const highlighted = computed(
  () => summary.value.active || summary.value.tone === 'warn' || summary.value.tone === 'danger',
)
const glow = computed(() => {
  if (lightColor.value) return lightLayers(lightColor.value).glow
  if (lit.value) return KELVIN_STOPS[0].glow
  return TONES[summary.value.tone].glow
})
const border = computed(() => {
  if (offline.value) return 'border-2 border-dashed border-state-offline/70'
  if (!highlighted.value) return 'hover:border-ring/40'
  if (lightColor.value) return lightLayers(lightColor.value).border
  return lit.value ? KELVIN_STOPS[0].border : TONES[summary.value.tone].border
})
/** A dimmer's brightness, drawn as a bar along the tile's bottom edge in the light's color. */
const level = computed(() => {
  if (!lit.value || !capabilityIds.value.includes('level')) return undefined
  const value = asNumber(read('level', 'level'))
  return value === undefined ? undefined : Math.min(100, Math.max(0, value))
})
const levelFill = computed(() =>
  lightColor.value ? lightLayers(lightColor.value).solid : KELVIN_STOPS[0].bg,
)
const iconTone = computed(() => iconToneOf(summary.value.tone, lit.value))
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
        border,
        offline && 'bg-muted/40 shadow-none',
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
          highlighted ? 'opacity-100' : 'opacity-0',
        )
      "
    />
    <div
      v-if="level !== undefined"
      aria-hidden="true"
      data-slot="tile-level"
      :class="
        cn(
          'pointer-events-none absolute bottom-0 left-0 h-1 rounded-r-full transition-[width] duration-500',
          levelFill,
        )
      "
      :style="{ width: `${level}%` }"
    />

    <div class="flex items-start justify-between gap-3">
      <!-- A favorite's star sits on the icon's corner: always in the same place, never in the name's way -->
      <span class="relative shrink-0">
        <DeviceIcon
          :icon="endpoint.icon ?? device.icon"
          :device-class="device.deviceClass"
          :capabilities="capabilityIds"
          :tone="iconTone"
          :open="open"
          :class="offline && 'opacity-60'"
        />
        <span
          v-if="endpoint.favorite"
          class="absolute -top-1.5 -right-1.5 flex size-5 items-center justify-center rounded-full border bg-card shadow-xs"
          data-slot="tile-favorite"
        >
          <Star
            class="size-3 fill-state-energy text-state-energy"
            :aria-label="t('device.favorite')"
            role="img"
          />
        </span>
      </span>
      <div v-if="quick" class="relative z-10 -mt-0.5 -mr-0.5">
        <CapabilityControl
          :device="device"
          :endpoint="endpoint"
          :capability="quick.capability"
          variant="tile"
        />
      </div>
      <span
        v-else-if="offline"
        class="inline-flex size-11 shrink-0 items-center justify-center rounded-full border-2 border-dashed border-state-offline/70 text-state-offline [&_svg]:size-5"
        data-slot="tile-offline"
        :title="t('device.availability.Offline')"
        aria-hidden="true"
      >
        <WifiOff />
      </span>
    </div>

    <div class="grid min-w-0 gap-1">
      <h3
        :id="nameId"
        :class="
          cn('flex min-w-0 items-center gap-1.5 leading-snug font-medium', offline && 'text-muted-foreground')
        "
      >
        <!-- Two lines at most, broken between words (never hyphenated) -->
        <RouterLink
          :to="{ name: 'device', params: { id: device.id } }"
          class="line-clamp-2 hyphens-manual outline-none after:absolute after:inset-0 after:content-['']"
        >
          {{ name }}
        </RouterLink>
        <Unlink
          v-if="device.isOrphaned"
          class="size-3.5 shrink-0 text-state-warn"
          :aria-label="t('device.orphaned')"
          role="img"
          data-slot="tile-orphaned"
        />
        <EyeOff
          v-if="endpoint.hidden"
          class="size-3.5 shrink-0 text-muted-foreground"
          :aria-label="t('device.hidden')"
          role="img"
        />
      </h3>
      <p v-if="showRoom" class="truncate text-xs text-muted-foreground">
        {{ roomName ?? t('device.noRoom') }}
      </p>
      <!-- One line: the state first, its details cut short (the device page has them all). -->
      <p
        class="min-w-0 truncate text-sm leading-snug"
        :title="
          stateLine.details.length > 0 ? `${stateLine.label} · ${stateLine.details.join(' · ')}` : undefined
        "
        data-slot="tile-state"
        :data-pending="pending ? (pending.awaitingPin ? 'pin' : 'true') : undefined"
      >
        <span
          aria-hidden="true"
          :class="cn('mr-1.5 inline-block size-2 rounded-full align-middle', TONES[summary.tone].dot)"
        />
        <span class="font-medium">{{ stateLine.label }}</span>
        <span v-if="stateLine.details.length > 0" class="text-muted-foreground tabular-nums">
          · {{ stateLine.details.join(' · ') }}
        </span>
      </p>
    </div>
  </article>
</template>
