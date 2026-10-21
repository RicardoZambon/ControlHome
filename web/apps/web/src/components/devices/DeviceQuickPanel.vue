<script setup lang="ts">
import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { cn } from '@controlhome/ui'
import { ChevronRight } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useEndpointSummary } from '../../composables/useEndpointSummary'
import { endpointName, useDevicesStore } from '../../stores/devices'
import DeviceIcon from './DeviceIcon.vue'
import StateBadge from './StateBadge.vue'
import { CapabilityControl, PowerToggle } from './capabilities'
import { layoutOf } from './endpointLayout'
import { capabilityLabel } from './format'
import { isLightEndpoint } from './lightColor'
import { TONES, iconToneOf } from './tone'

/**
 * A device's controls in a small panel (the floor plan's tap-to-control popover, a sheet on phones): its name, room and
 * state, then each endpoint's controls and readings from the one capability-control registry, and a link to the full
 * device page. Read-only for Viewers (the controls themselves know who may use them).
 */
const props = defineProps<{
  device: DeviceSummaryDto
  /** The endpoints to control: one placed endpoint, or the visible endpoints of a device placed whole. */
  endpoints: EndpointDto[]
  /** The name to show (an endpoint's own name, or the device's). */
  name: string
  roomName?: string
}>()

defineEmits<{ navigate: [] }>()

const { t, te } = useI18n()
const devices = useDevicesStore()
const { summaryOf } = useEndpointSummary()

const availability = computed(() => devices.availabilityOf(props.device))
const multiple = computed(() => props.endpoints.length > 1)
const sections = computed(() =>
  props.endpoints.map((endpoint) => ({
    endpoint,
    layout: layoutOf(endpoint),
    summary: summaryOf(props.device, endpoint),
  })),
)
const lead = computed(() => sections.value[0]?.summary)
const capabilityIds = computed(() =>
  props.endpoints.flatMap((endpoint) => endpoint.capabilities.map((capability) => capability.capabilityId)),
)
const iconTone = computed(() =>
  lead.value
    ? iconToneOf(lead.value.tone, isLightEndpoint(capabilityIds.value, props.device.deviceClass))
    : 'neutral',
)
</script>

<template>
  <div class="grid gap-4" data-slot="device-quick-panel" :data-device-id="device.id">
    <header class="flex items-start gap-3">
      <DeviceIcon
        :icon="endpoints[0]?.icon ?? device.icon"
        :device-class="device.deviceClass"
        :capabilities="capabilityIds"
        :tone="iconTone"
      />
      <div class="grid min-w-0 flex-1 gap-0.5">
        <p class="eyebrow truncate">{{ roomName ?? t('device.noRoom') }}</p>
        <h2 class="truncate font-serif text-xl leading-tight font-medium tracking-tight">{{ name }}</h2>
        <p v-if="lead && !multiple" class="flex items-center gap-1.5 text-sm" data-slot="quick-state">
          <span aria-hidden="true" :class="cn('inline-block size-2 rounded-full', TONES[lead.tone].dot)" />
          <span class="font-medium">{{ lead.label }}</span>
          <span v-if="lead.details.length > 0" class="truncate text-muted-foreground tabular-nums">
            · {{ lead.details.join(' · ') }}
          </span>
        </p>
      </div>
    </header>

    <div v-if="availability !== 'Online' || device.isOrphaned" class="flex flex-wrap gap-2">
      <StateBadge v-if="availability !== 'Online'" :availability="availability" />
      <StateBadge v-if="device.isOrphaned" :availability="availability" orphaned />
    </div>

    <section
      v-for="{ endpoint, layout, summary } in sections"
      :key="endpoint.id"
      class="grid gap-3"
      :aria-label="endpointName(device, endpoint)"
    >
      <h3 v-if="multiple" class="flex items-center gap-2 text-sm font-medium">
        <span aria-hidden="true" :class="cn('inline-block size-2 rounded-full', TONES[summary.tone].dot)" />
        {{ endpoint.name ?? endpoint.key }}
        <span class="font-normal text-muted-foreground">· {{ summary.label }}</span>
      </h3>
      <div
        v-for="entry in layout.controls"
        :key="entry.capability.id"
        class="rounded-xl border bg-card p-4"
        :data-capability="entry.capability.capabilityId"
      >
        <div class="mb-3 flex min-h-8 items-center justify-between gap-3">
          <h4 class="eyebrow">{{ capabilityLabel(entry.capability.capabilityId, t, te) }}</h4>
          <PowerToggle v-if="entry.power" :device="device" :endpoint="endpoint" :capability="entry.power" />
        </div>
        <CapabilityControl :device="device" :endpoint="endpoint" :capability="entry.capability" />
      </div>
      <div v-if="layout.readings.length > 0" class="grid grid-cols-2 items-start gap-2" data-slot="readings">
        <CapabilityControl
          v-for="capability in layout.readings"
          :key="capability.id"
          :device="device"
          :endpoint="endpoint"
          :capability="capability"
        />
      </div>
    </section>

    <RouterLink
      :to="{ name: 'device', params: { id: device.id } }"
      class="group flex items-center justify-between gap-2 rounded-lg border bg-muted/40 px-3 py-2.5 text-sm font-medium text-primary outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring"
      @click="$emit('navigate')"
    >
      {{ t('floorplan.devices.openDevice') }}
      <ChevronRight class="size-4 transition-transform group-hover:translate-x-0.5" aria-hidden="true" />
    </RouterLink>
  </div>
</template>
