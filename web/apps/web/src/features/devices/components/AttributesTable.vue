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
 * Every attribute of the device with its live value and when it was last observed: a table, or a stacked list on
 * phones. Admins also see each one's AttributeId (the key of its state and history). "Last known value" marks a value
 * loaded from the database that the running connector has not confirmed yet.
 */
const props = defineProps<{
  device: Pick<DeviceSummaryDto, 'name' | 'endpoints'>
  endpoints: readonly EndpointDto[]
  /** Show the AttributeIds (a diagnostic detail for Admins). */
  showIds?: boolean
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
  <template v-else>
    <!-- Phones: one stacked row per attribute, the value on the right and when it was updated below -->
    <ul class="divide-y sm:hidden" data-slot="attributes-list">
      <li v-for="row in rows" :key="row.id" class="grid gap-0.5 py-3" :data-attribute-id="row.id">
        <div class="flex items-baseline justify-between gap-4">
          <span class="min-w-0 font-medium break-words">{{ row.attribute }}</span>
          <span class="shrink-0 text-right font-medium tabular-nums">{{ row.value }}</span>
        </div>
        <span class="text-xs text-muted-foreground">
          <template v-if="row.endpoint">{{ row.endpoint }} · </template>{{ row.capability }} ·
          <time v-if="row.observed" :datetime="row.observed">{{ when(row.observed) }}</time
          ><template v-else>{{ t('device.noValue') }}</template
          ><template v-if="row.restored"> · {{ t('devices.attributes.restored') }}</template
          ><template v-if="showIds">
            · <code class="font-mono">#{{ row.id }}</code></template
          >
        </span>
      </li>
    </ul>

    <Table class="table-fixed max-sm:hidden" data-slot="attributes-table">
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
              <template v-if="row.endpoint">{{ row.endpoint }} · </template>{{ row.capability
              }}<template v-if="showIds">
                · <code class="font-mono">#{{ row.id }}</code></template
              >
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
</template>
