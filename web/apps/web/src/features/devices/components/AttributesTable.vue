<script setup lang="ts">
import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { Badge, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@controlhome/ui'
import { useNow } from '@vueuse/core'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { attributeLabel, capabilityLabel, formatStateValue } from '../../../components/devices/format'
import { capitalize, formatDateTime, formatRelative } from '../../../lib/format'
import { endpointName, useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'

/**
 * Diagnostics: every attribute of the device with its live value, when it was last observed and its AttributeId
 * (the key of its state and history). "Restored" marks a value loaded from the database that the running connector has
 * not confirmed yet.
 */
const props = defineProps<{
  device: Pick<DeviceSummaryDto, 'name' | 'endpoints'>
  endpoints: readonly EndpointDto[]
}>()

const { t, te, locale } = useI18n()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const now = useNow({ interval: 30_000 })

const rows = computed(() =>
  props.endpoints.flatMap((endpoint) =>
    endpoint.capabilities.flatMap((capability) => {
      const definition = devices.definitionOf(capability)
      return capability.attributes.map((attribute) => {
        const schema = definition?.attributes.find((entry) => entry.key === attribute.key)
        const live = realtime.liveOf(attribute.attributeId)
        const value = realtime.values.has(attribute.attributeId) ? live?.value : attribute.value
        const observed = live?.observedAtUtc ?? attribute.observedAtUtc
        return {
          id: attribute.attributeId,
          endpoint: props.endpoints.length > 1 ? endpointName(props.device, endpoint) : undefined,
          capability: capabilityLabel(capability.capabilityId, t, te),
          retired: capability.retired,
          attribute: attributeLabel(attribute.key, t, te, capability.capabilityId),
          key: `${capability.capabilityId}.${attribute.key}`,
          diagnostic: schema?.category === 'diagnostic',
          value: formatStateValue(value, {
            attributeKey: attribute.key,
            schema: schema?.schema,
            unit: schema?.unit,
            locale: locale.value,
            t,
            te,
          }),
          restored: live ? live.restored : attribute.restored,
          observed,
        }
      })
    }),
  ),
)

function when(observed: string | null): string {
  if (!observed) return t('device.noValue')
  return capitalize(formatRelative(observed, now.value, locale.value, t('common.justNow')), locale.value)
}
</script>

<template>
  <p v-if="rows.length === 0" class="text-sm text-muted-foreground">{{ t('devices.attributes.empty') }}</p>
  <Table v-else class="table-fixed" data-slot="attributes-table">
    <TableHeader>
      <TableRow>
        <TableHead class="w-[42%]">{{ t('devices.attributes.attribute') }}</TableHead>
        <TableHead class="w-[33%]">{{ t('devices.attributes.value') }}</TableHead>
        <TableHead>{{ t('devices.attributes.observed') }}</TableHead>
      </TableRow>
    </TableHeader>
    <TableBody>
      <TableRow v-for="row in rows" :key="row.id" :data-attribute-id="row.id">
        <TableCell class="align-top whitespace-normal">
          <span class="block font-medium break-words">{{ row.attribute }}</span>
          <span class="block text-xs break-words text-muted-foreground">
            <template v-if="row.endpoint">{{ row.endpoint }} · </template>{{ row.capability }} ·
            <code class="font-mono">#{{ row.id }}</code>
          </span>
          <span v-if="row.diagnostic || row.retired" class="mt-1 flex flex-wrap gap-1">
            <Badge v-if="row.diagnostic" variant="outline" class="text-muted-foreground">
              {{ t('devices.attributes.diagnostic') }}
            </Badge>
            <Badge v-if="row.retired" variant="outline" class="text-muted-foreground">
              {{ t('devices.attributes.retired') }}
            </Badge>
          </span>
        </TableCell>
        <TableCell class="align-top whitespace-normal">
          <span class="block font-medium break-words tabular-nums" data-slot="attribute-value">{{
            row.value
          }}</span>
          <span v-if="row.restored" class="block text-xs text-muted-foreground">
            {{ t('devices.attributes.restored') }}
          </span>
        </TableCell>
        <TableCell class="align-top text-xs whitespace-normal text-muted-foreground">
          <time v-if="row.observed" :datetime="row.observed" :title="formatDateTime(row.observed, locale)">
            {{ when(row.observed) }}
          </time>
          <template v-else>{{ t('device.noValue') }}</template>
        </TableCell>
      </TableRow>
    </TableBody>
  </Table>
</template>
