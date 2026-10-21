<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed } from 'vue'
import { useEndpointSummary } from '../../composables/useEndpointSummary'
import { standsOpen } from '../devices/deviceState'
import { deviceIcon } from '../devices/deviceIcons'
import { isLightEndpoint } from '../devices/lightColor'
import { iconToneOf } from '../devices/tone'
import { MARKER_RADIUS_PX, MARKER_TONES, combineSummaries } from './markerTone'
import type { PlanMarker } from './placements'

/**
 * A device on the floor plan: its icon in a disc whose ring has the live state color (on, off, a dashed ring when it
 * can't be reached, warning and alarm colors), with a soft halo while it is on or running. Sizes are in screen pixels
 * (`scale` = px per cm), so markers keep their size at any zoom. `compact` (dashboard previews) draws a small dot.
 * Keyboard: a focusable button when `focusable` (Enter or Space activates it); the name shows on hover and focus.
 */
const props = withDefaults(
  defineProps<{
    marker: PlanMarker
    scale: number
    compact?: boolean
    coarse?: boolean
    selected?: boolean
    dragging?: boolean
    /** Drawn at this point instead of the placement's (while it is dragged). */
    at?: { x: number; y: number } | null
    focusable?: boolean
    /** Shown dimmed (the editor's other tabs). */
    muted?: boolean
    /**
     * The name stays shown while selected (the editor). Off in view mode, where the selected device's panel, open
     * beside it, already names it, and the label would only cover the plan.
     */
    nameOnSelect?: boolean
  }>(),
  {
    compact: false,
    coarse: false,
    selected: false,
    dragging: false,
    at: null,
    focusable: false,
    muted: false,
    nameOnSelect: true,
  },
)

const emit = defineEmits<{ activate: [] }>()

const { readerOf, summaryOf } = useEndpointSummary()

const summary = computed(() =>
  combineSummaries(props.marker.endpoints.map((endpoint) => summaryOf(props.marker.device, endpoint))),
)
const capabilities = computed(() =>
  props.marker.endpoints.flatMap((endpoint) =>
    endpoint.capabilities.filter((capability) => !capability.retired).map((c) => c.capabilityId),
  ),
)
const icon = computed(() => {
  const endpoint = props.marker.endpoint ?? props.marker.endpoints[0]
  const open =
    endpoint && summary.value.tone !== 'offline' ? standsOpen(capabilities.value, readerOf(endpoint)) : false
  return deviceIcon({
    icon: props.marker.endpoint?.icon ?? props.marker.device.icon,
    deviceClass: props.marker.device.deviceClass,
    capabilities: capabilities.value,
    open,
  })
})
const tone = computed(() => MARKER_TONES[summary.value.tone])
const iconTone = computed(
  () =>
    MARKER_TONES[
      iconToneOf(summary.value.tone, isLightEndpoint(capabilities.value, props.marker.device.deviceClass))
    ].icon,
)

const radius = computed(() =>
  props.compact ? MARKER_RADIUS_PX.compact : props.coarse ? MARKER_RADIUS_PX.coarse : MARKER_RADIUS_PX.fine,
)
const px = (value: number) => value / props.scale
const x = computed(() => props.at?.x ?? props.marker.placement.xCm)
const y = computed(() => props.at?.y ?? props.marker.placement.yCm)
const iconPx = computed(() => (props.coarse ? 18 : 16))
const label = computed(() =>
  summary.value.label ? `${props.marker.name}, ${summary.value.label}` : props.marker.name,
)
const offline = computed(() => summary.value.tone === 'offline')
</script>

<template>
  <g
    :transform="`translate(${x} ${y})`"
    data-slot="placed-device"
    :data-placement-id="marker.placement.id"
    :data-tone="summary.tone"
    :data-active="summary.active || undefined"
    :role="focusable ? 'button' : undefined"
    :tabindex="focusable ? 0 : undefined"
    :aria-label="focusable ? label : undefined"
    :aria-hidden="focusable ? undefined : 'true'"
    :class="
      cn(
        'group/marker outline-none',
        focusable && 'cursor-pointer',
        muted && 'opacity-45',
        dragging && 'cursor-grabbing',
      )
    "
    @keydown.enter.prevent="emit('activate')"
    @keydown.space.prevent="emit('activate')"
  >
    <title v-if="!compact">{{ label }}</title>

    <template v-if="compact">
      <circle
        :r="px(radius)"
        :class="cn(summary.active ? tone.dot : 'fill-card', tone.ring)"
        :stroke-width="px(1.75)"
        :stroke-dasharray="tone.dashed ? `${px(2)} ${px(1.5)}` : undefined"
      />
    </template>

    <template v-else>
      <!-- On or running: a soft halo -->
      <circle
        v-if="summary.active || summary.tone === 'danger'"
        :r="px(radius + 7)"
        :class="cn(tone.halo, 'motion-safe:animate-pulse')"
        data-slot="marker-halo"
      />
      <!-- Selected (the open quick panel, the editor's selection) or focused from the keyboard -->
      <circle
        :r="px(radius + 4.5)"
        fill="none"
        stroke-width="2"
        vector-effect="non-scaling-stroke"
        :class="
          cn(
            'stroke-ring transition-opacity',
            selected ? 'opacity-100' : 'opacity-0 group-focus-visible/marker:opacity-100',
          )
        "
      />
      <circle
        :r="px(radius)"
        :class="
          cn(
            'fill-card transition-[stroke] duration-300',
            tone.ring,
            dragging ? 'drop-shadow-lg' : 'drop-shadow-sm',
          )
        "
        :stroke-width="px(2.5)"
        :stroke-dasharray="tone.dashed ? `${px(3.5)} ${px(2.5)}` : undefined"
        data-slot="marker-ring"
      />
      <component
        :is="icon"
        :x="-px(iconPx / 2)"
        :y="-px(iconPx / 2)"
        :width="px(iconPx)"
        :height="px(iconPx)"
        :class="cn('pointer-events-none', iconTone, offline && 'opacity-70')"
        aria-hidden="true"
      />
      <!-- The name, on hover, focus or selection -->
      <text
        :y="px(radius + 15)"
        text-anchor="middle"
        :font-size="px(11.5)"
        paint-order="stroke"
        :stroke-width="px(4)"
        stroke-linejoin="round"
        :class="
          cn(
            'pointer-events-none fill-foreground stroke-card font-sans font-medium transition-opacity',
            (selected && nameOnSelect) || dragging
              ? 'opacity-100'
              : 'opacity-0 group-hover/marker:opacity-100 group-focus-visible/marker:opacity-100',
          )
        "
      >
        {{ marker.name }}
      </text>
    </template>
  </g>
</template>
