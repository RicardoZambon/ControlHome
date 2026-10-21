<script setup lang="ts">
import type { DeviceDetailDto } from '@controlhome/api-client'
import { ToggleGroup, ToggleGroupItem } from '@controlhome/ui'
import { History } from '@lucide/vue'
import { useIntervalFn } from '@vueuse/core'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  HISTORY_RANGES,
  historyWindow,
  isHistoryRange,
  type HistoryRange,
} from '../../../components/charts/chartOptions'
import { serverNow } from '../../../lib/serverClock'
import { endpointName, useDevicesStore } from '../../../stores/devices'
import { energyEndpoints, recordedAttributes } from '../history'
import AttributeHistoryCard from './AttributeHistoryCard.vue'
import EnergyHistoryCard from './EnergyHistoryCard.vue'

/**
 * The device page's History tab: a range (24 h, 7 days, 30 days), the energy it used (bars) when it has a meter or power
 * readings, and a card for every recorded attribute. Every card covers the same window — the range up to the server's
 * now — so cards side by side line up; it moves on every minute while the tab is open.
 */
const props = defineProps<{ device: DeviceDetailDto }>()
const range = defineModel<HistoryRange>('range', { default: '24h' })

const { t } = useI18n()
const devices = useDevicesStore()

/** The window's end: the server's now when the tab opened, then every minute. */
const end = ref(serverNow())
useIntervalFn(() => (end.value = serverNow()), 60_000)
const chartWindow = computed(() => historyWindow(range.value, end.value))

const multiple = computed(() => props.device.endpoints.length > 1)
const attributes = computed(() =>
  recordedAttributes(props.device.endpoints, (capability) => devices.definitionOf(capability)),
)
const energy = computed(() => energyEndpoints(props.device.endpoints))

function setRange(value: unknown): void {
  if (!isHistoryRange(value)) return
  end.value = serverNow()
  range.value = value
}
</script>

<template>
  <div class="grid gap-6" data-slot="device-history">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm text-muted-foreground">{{ t('history.description') }}</p>
      <ToggleGroup
        type="single"
        variant="outline"
        :model-value="range"
        :aria-label="t('history.range')"
        data-slot="history-range"
        @update:model-value="setRange"
      >
        <ToggleGroupItem
          v-for="option in HISTORY_RANGES"
          :key="option"
          :value="option"
          class="px-3 tabular-nums"
        >
          {{ t(`history.rangesShort.${option}`) }}
        </ToggleGroupItem>
      </ToggleGroup>
    </div>

    <EnergyHistoryCard v-if="energy.length > 0" :endpoints="energy" :range="range" :window="chartWindow" />

    <div v-if="attributes.length > 0" class="grid items-start gap-4 lg:grid-cols-2">
      <AttributeHistoryCard
        v-for="entry in attributes"
        :key="entry.attribute.attributeId"
        :entry="entry"
        :range="range"
        :window="chartWindow"
        :endpoint-name="multiple ? endpointName(device, entry.endpoint) : undefined"
      />
    </div>

    <section
      v-else-if="energy.length === 0"
      class="grid justify-items-center gap-3 rounded-2xl border border-dashed px-6 py-12 text-center"
    >
      <History class="size-8 text-muted-foreground" aria-hidden="true" />
      <p class="max-w-sm text-sm text-muted-foreground">{{ t('history.nothingRecorded') }}</p>
    </section>
  </div>
</template>
