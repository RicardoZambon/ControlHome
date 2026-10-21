<script setup lang="ts">
import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { Button, Input, Skeleton, cn } from '@controlhome/ui'
import { ChevronRight, GripVertical, MapPin, MapPinOff, Search, X } from '@lucide/vue'
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import {
  matchesSearch,
  normalizeText,
  type PlacementTarget,
  type PlanMarker,
} from '../../../components/floorplan/placements'
import { endpointName, useDevicesStore } from '../../../stores/devices'
import { useEditor } from './useEditorState'
import { usePlacementEditorContext } from './usePlacementEditor'

/**
 * The editor's Devices tab: the selected device on the plan (open it, take it off the plan), the devices on this floor,
 * and everything that is on no plan yet — drag a row by its handle onto the plan, or press "Place" and tap the spot
 * (touch screens, keyboard). A device with several controls goes on the plan as a whole or control by control.
 */
const { t } = useI18n()
const state = useEditor()
const editor = usePlacementEditorContext()
const devices = useDevicesStore()
const searchId = useId()

const roomsById = computed(() => new Map((state.server.value?.rooms ?? []).map((room) => [room.id, room])))
const roomName = (marker: PlanMarker) => {
  const roomId = marker.placement.roomId
  return roomId === null ? t('floors.editor.devices.outside') : (roomsById.value.get(roomId)?.name ?? '')
}

const query = computed(() => editor.search.value)
const groups = computed(() => editor.groups.value.filter((group) => matchesSearch(group, query.value)))
const placed = computed(() =>
  editor.markers.value
    .filter((marker) => !query.value || normalizeText(marker.name).includes(normalizeText(query.value)))
    .sort((a, b) => a.name.localeCompare(b.name)),
)
const selected = computed(() => editor.selected.value)

function capabilitiesOf(endpoints: readonly EndpointDto[]): string[] {
  return endpoints.flatMap((endpoint) => endpoint.capabilities.map((capability) => capability.capabilityId))
}

function rowsOf(device: DeviceSummaryDto, endpoints: EndpointDto[], whole: boolean) {
  const rows: {
    key: string
    target: PlacementTarget
    name: string
    detail: string
    nested: boolean
    endpoints: EndpointDto[]
  }[] = []
  if (whole) {
    rows.push({
      key: `d:${device.id}`,
      target: { deviceId: device.id },
      name: device.name,
      detail: t('floors.editor.devices.whole', { count: endpoints.length }, endpoints.length),
      nested: false,
      endpoints,
    })
  }
  if (endpoints.length > 1 || whole) {
    for (const endpoint of endpoints)
      rows.push({
        key: `e:${endpoint.id}`,
        target: { endpointId: endpoint.id },
        name: endpoint.name ?? endpoint.key,
        detail: '',
        nested: true,
        endpoints: [endpoint],
      })
  } else {
    const endpoint = endpoints[0]!
    rows.push({
      key: `e:${endpoint.id}`,
      target: { endpointId: endpoint.id },
      name: endpointName(device, endpoint),
      detail: '',
      nested: false,
      endpoints: [endpoint],
    })
  }
  return rows
}

const unplaced = computed(() =>
  groups.value.map((group) => ({
    device: group.device,
    header: !group.whole && group.endpoints.length > 1,
    rows: rowsOf(group.device, group.endpoints, group.whole),
  })),
)

const nothingAtAll = computed(() => devices.loaded && devices.devices.length === 0)
</script>

<template>
  <section class="grid gap-5" :aria-label="t('floors.editor.tabs.devices')" data-slot="editor-devices">
    <!-- The device selected on the plan -->
    <div
      v-if="selected"
      class="grid gap-3 rounded-xl border border-ring/40 bg-accent/40 p-3"
      data-slot="selected-placement"
    >
      <div class="flex items-center gap-3">
        <DeviceIcon
          size="sm"
          :icon="selected.endpoint?.icon ?? selected.device.icon"
          :device-class="selected.device.deviceClass"
          :capabilities="capabilitiesOf(selected.endpoints)"
        />
        <div class="grid min-w-0 flex-1">
          <span class="truncate text-sm font-medium">{{ selected.name }}</span>
          <span class="truncate text-xs text-muted-foreground">{{ roomName(selected) }}</span>
        </div>
        <Button
          variant="ghost"
          size="icon-sm"
          :aria-label="t('floors.editor.devices.deselect')"
          @click="editor.select(null)"
        >
          <X />
        </Button>
      </div>
      <p class="text-xs text-muted-foreground">{{ t('floors.editor.devices.moveHint') }}</p>
      <div class="flex flex-wrap gap-2">
        <Button
          variant="outline"
          size="sm"
          :loading="editor.busy.value"
          data-slot="remove-placement"
          @click="editor.remove(selected.placement.id)"
        >
          <MapPinOff />
          {{ t('floors.editor.devices.remove') }}
        </Button>
        <Button variant="ghost" size="sm" as-child>
          <RouterLink :to="{ name: 'device', params: { id: selected.device.id } }">
            {{ t('floorplan.devices.openDevice') }}
            <ChevronRight />
          </RouterLink>
        </Button>
      </div>
    </div>

    <div class="relative">
      <label :for="searchId" class="sr-only">{{ t('floors.editor.devices.search') }}</label>
      <Search
        class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
        aria-hidden="true"
      />
      <Input
        :id="searchId"
        v-model="editor.search.value"
        type="search"
        class="pl-9"
        :placeholder="t('floors.editor.devices.search')"
      />
    </div>

    <div
      v-if="!devices.loaded && devices.loading"
      class="grid gap-2"
      role="status"
      :aria-label="t('common.loading')"
    >
      <Skeleton v-for="index in 4" :key="index" class="h-11 w-full" />
    </div>

    <p v-else-if="nothingAtAll" class="text-sm text-muted-foreground">
      {{ t('floors.editor.devices.none') }}
      <RouterLink
        :to="{ name: 'integrations' }"
        class="font-medium text-primary underline-offset-4 hover:underline"
      >
        {{ t('floors.editor.devices.addIntegration') }}
      </RouterLink>
    </p>

    <template v-else>
      <!-- On no plan yet -->
      <section class="grid gap-2" aria-labelledby="devices-unplaced">
        <div class="flex items-baseline justify-between gap-2">
          <h3 id="devices-unplaced" class="eyebrow">{{ t('floors.editor.devices.unplaced') }}</h3>
          <span class="text-xs text-muted-foreground tabular-nums">{{ editor.groups.value.length }}</span>
        </div>
        <p v-if="editor.groups.value.length > 0" class="text-xs text-muted-foreground">
          {{ t('floors.editor.devices.hint') }}
        </p>
        <p v-if="editor.groups.value.length === 0" class="text-sm text-muted-foreground">
          {{ t('floors.editor.devices.allPlaced') }}
        </p>
        <p v-else-if="unplaced.length === 0" class="text-sm text-muted-foreground">
          {{ t('common.noResults') }}
        </p>
        <ul class="-mx-2 grid gap-0.5" data-slot="unplaced-list">
          <li v-for="group in unplaced" :key="group.device.id">
            <p
              v-if="group.header"
              class="truncate px-2 pt-1.5 pb-0.5 text-xs font-medium text-muted-foreground"
            >
              {{ group.device.name }}
            </p>
            <ul class="grid gap-0.5">
              <li
                v-for="row in group.rows"
                :key="row.key"
                :class="
                  cn(
                    'group flex items-center gap-1.5 rounded-lg py-1 pr-1 pl-0.5 transition-colors hover:bg-accent/60',
                    row.nested && 'ml-6',
                    editor.isBusyWith(row.target) && 'bg-accent ring-1 ring-ring/40',
                  )
                "
                :data-target="row.key"
                data-slot="unplaced-device"
              >
                <button
                  type="button"
                  class="flex size-8 shrink-0 cursor-grab touch-none items-center justify-center rounded-md text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring active:cursor-grabbing"
                  :aria-label="t('floors.editor.devices.drag', { name: row.name })"
                  :title="t('floors.editor.devices.drag', { name: row.name })"
                  data-slot="drag-handle"
                  @pointerdown="editor.startDrag(row.target, row.name, $event)"
                  @pointermove="editor.moveDrag"
                  @pointerup="editor.endDrag"
                  @pointercancel="editor.cancelDrag"
                  @click.prevent
                >
                  <GripVertical class="size-4" aria-hidden="true" />
                </button>
                <DeviceIcon
                  v-if="!row.nested"
                  size="sm"
                  :icon="row.endpoints[0]?.icon ?? group.device.icon"
                  :device-class="group.device.deviceClass"
                  :capabilities="capabilitiesOf(row.endpoints)"
                />
                <span class="grid min-w-0 flex-1">
                  <span class="truncate text-sm font-medium">{{ row.name }}</span>
                  <span v-if="row.detail" class="truncate text-xs text-muted-foreground">{{
                    row.detail
                  }}</span>
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  class="shrink-0"
                  :aria-label="t('floors.editor.devices.placeNamed', { name: row.name })"
                  @click="editor.arm(row.target, row.name)"
                >
                  <MapPin />
                  {{ t('floors.editor.devices.place') }}
                </Button>
              </li>
            </ul>
          </li>
        </ul>
      </section>

      <!-- On this floor -->
      <section class="grid gap-2" aria-labelledby="devices-placed">
        <div class="flex items-baseline justify-between gap-2">
          <h3 id="devices-placed" class="eyebrow">{{ t('floors.editor.devices.onFloor') }}</h3>
          <span class="text-xs text-muted-foreground tabular-nums">{{ editor.markers.value.length }}</span>
        </div>
        <p v-if="editor.markers.value.length === 0" class="text-sm text-muted-foreground">
          {{ t('floors.editor.devices.noneHere') }}
        </p>
        <ul v-else class="-mx-2 grid gap-0.5" data-slot="placed-list">
          <li
            v-for="marker in placed"
            :key="marker.placement.id"
            :class="
              cn(
                'flex items-center gap-1 rounded-lg pr-1 transition-colors hover:bg-accent/60',
                editor.selectedId.value === marker.placement.id && 'bg-accent',
              )
            "
          >
            <button
              type="button"
              class="flex min-w-0 flex-1 items-center gap-3 rounded-lg px-2 py-1.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring"
              :aria-pressed="editor.selectedId.value === marker.placement.id"
              @click="editor.select(marker.placement.id)"
            >
              <DeviceIcon
                size="sm"
                :icon="marker.endpoint?.icon ?? marker.device.icon"
                :device-class="marker.device.deviceClass"
                :capabilities="capabilitiesOf(marker.endpoints)"
              />
              <span class="grid min-w-0 flex-1">
                <span class="truncate text-sm font-medium">{{ marker.name }}</span>
                <span class="truncate text-xs text-muted-foreground">{{ roomName(marker) }}</span>
              </span>
            </button>
            <Button
              variant="ghost"
              size="icon-sm"
              class="shrink-0 text-muted-foreground hover:text-destructive"
              :aria-label="t('floors.editor.devices.removeNamed', { name: marker.name })"
              :disabled="editor.busy.value"
              @click="editor.remove(marker.placement.id)"
            >
              <MapPinOff />
            </Button>
          </li>
        </ul>
        <p v-if="editor.elsewhere.value > 0" class="text-xs text-muted-foreground">
          {{ t('floors.editor.devices.elsewhere', editor.elsewhere.value) }}
        </p>
      </section>
    </template>
  </section>
</template>
