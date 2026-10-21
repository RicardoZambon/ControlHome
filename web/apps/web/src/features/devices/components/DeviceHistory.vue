<script setup lang="ts">
import type { DeviceDetailDto } from '@controlhome/api-client'
import { ToggleGroup, ToggleGroupItem } from '@controlhome/ui'
import { History } from '@lucide/vue'
import { useIntervalFn } from '@vueuse/core'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { HISTORY_RANGES, isHistoryRange, type HistoryRange } from '../../../components/charts/chartOptions'
import { endpointName, useDevicesStore } from '../../../stores/devices'
import { energyEndpoints, recordedAttributes } from '../history'
import AttributeHistoryCard from './AttributeHistoryCard.vue'
import EnergyHistoryCard from './EnergyHistoryCard.vue'

/**
 * The device page's History tab: a range (24 h, 7 days, 30 days), the energy it used (bars) when it has a meter or power
 * readings, and a chart for every recorded attribute. Refreshed every minute while open.
 */
const props = defineProps<{ device: DeviceDetailDto }>()
const range = defineModel<HistoryRange>('range', { default: '24h' })

const { t } = useI18n()
const devices = useDevicesStore()

const refresh = ref(0)
useIntervalFn(() => (refresh.value += 1), 60_000)

const multiple = computed(() => props.device.endpoints.length > 1)
const attributes = computed(() =>
  recordedAttributes(props.device.endpoints, (capability) => devices.definitionOf(capability)),
)
const energy = computed(() => energyEndpoints(props.device.endpoints))

function setRange(value: unknown): void {
  if (isHistoryRange(value)) range.value = value
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

    <EnergyHistoryCard v-if="energy.length > 0" :endpoints="energy" :range="range" :refresh="refresh" />

    <div v-if="attributes.length > 0" class="grid items-start gap-4 lg:grid-cols-2">
      <AttributeHistoryCard
        v-for="entry in attributes"
        :key="entry.attribute.attributeId"
        :entry="entry"
        :range="range"
        :refresh="refresh"
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
