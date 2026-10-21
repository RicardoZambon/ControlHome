<script setup lang="ts">
import {
  Activity,
  AlarmSmoke,
  CircleCheck,
  DoorClosed,
  DoorOpen,
  Droplets,
  Footprints,
  House,
  PersonStanding,
} from '@lucide/vue'
import { computed, type Component } from 'vue'
import ReadingTile from '../controls/ReadingTile.vue'
import type { Tone } from '../deviceState'
import { capabilityLabel } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'
import { useUpdated } from './useUpdated'

/**
 * Yes/no sensors in words: `contact` (open/closed), `motion`, `occupancy`, `presence` (home/away), `smoke` and
 * `waterLeak` (alarms in the danger color).
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te } = control
const updated = useUpdated()

interface Face {
  label: string
  tone: Tone
  icon: Component
}

interface Config {
  key: string
  yes: Face
  no: Face
}

const CONFIGS: Record<string, Config> = {
  contact: {
    key: 'open',
    yes: { label: 'device.reading.open', tone: 'warn', icon: DoorOpen },
    no: { label: 'device.reading.closed', tone: 'off', icon: DoorClosed },
  },
  motion: {
    key: 'motion',
    yes: { label: 'device.reading.motion', tone: 'info', icon: Activity },
    no: { label: 'device.reading.noMotion', tone: 'off', icon: Activity },
  },
  occupancy: {
    key: 'occupied',
    yes: { label: 'device.reading.occupied', tone: 'info', icon: PersonStanding },
    no: { label: 'device.reading.clear', tone: 'off', icon: PersonStanding },
  },
  presence: {
    key: 'present',
    yes: { label: 'device.reading.home', tone: 'on', icon: House },
    no: { label: 'device.reading.away', tone: 'off', icon: Footprints },
  },
  smoke: {
    key: 'smoke',
    yes: { label: 'device.reading.smoke', tone: 'danger', icon: AlarmSmoke },
    no: { label: 'device.reading.noSmoke', tone: 'neutral', icon: CircleCheck },
  },
  waterLeak: {
    key: 'leak',
    yes: { label: 'device.reading.leak', tone: 'danger', icon: Droplets },
    no: { label: 'device.reading.dry', tone: 'neutral', icon: CircleCheck },
  },
}

const config = computed(() => CONFIGS[props.capability.capabilityId] ?? CONFIGS.contact!)
const value = computed(() => control.state.bool(config.value.key))
const face = computed<Face | undefined>(() =>
  value.value === undefined ? undefined : value.value ? config.value.yes : config.value.no,
)
</script>

<template>
  <ReadingTile
    :icon="face?.icon ?? config.no.icon"
    :label="capabilityLabel(capability.capabilityId, t, te)"
    :value="face ? t(face.label) : t('device.state.unknown')"
    :tone="face?.tone ?? 'neutral'"
    :caption="updated(control.state.live(config.key))"
    word
  />
</template>
