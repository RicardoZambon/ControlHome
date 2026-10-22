import {
  listCapabilities,
  listDevices,
  listIntegrations,
  listRooms,
  type CapabilityDto,
  type DeviceDetailDto,
  type DeviceSummaryDto,
  type EndpointCapabilityDto,
  type EndpointDto,
  type IntegrationDto,
  type RoomListItemDto,
} from '@controlhome/api-client'
import { defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { unwrap } from '../lib/problem'
import { useRealtimeStore } from './realtime'

/** One controllable part of a device, as the device grid shows it (a tile per endpoint). */
export interface EndpointEntry {
  device: DeviceSummaryDto
  endpoint: EndpointDto
  /** The endpoint's room (its own, else the device's), or `null`. */
  roomId: number | null
}

/** How long a burst of `DevicesChanged` messages is collected before the lists reload. */
export const RELOAD_DEBOUNCE_MS = 300

/** The device's own name, or the endpoint's when it has one; multi-endpoint devices name each endpoint. */
export function endpointName(
  device: Pick<DeviceSummaryDto, 'name' | 'endpoints'>,
  endpoint: EndpointDto,
): string {
  if (endpoint.name) return endpoint.name
  return device.endpoints.length > 1 ? `${device.name} · ${endpoint.key}` : device.name
}

/**
 * The endpoint named with its device wherever it stands alone (a plan, a list of what to place): "Living room wall
 * switch · Ceiling" rather than just "Ceiling".
 */
export function fullEndpointName(
  device: Pick<DeviceSummaryDto, 'name' | 'endpoints'>,
  endpoint: EndpointDto,
): string {
  const name = endpointName(device, endpoint)
  return endpoint.name && device.endpoints.length > 1 && endpoint.name !== device.name
    ? `${device.name} · ${name}`
    : name
}

/** A device detail reduced to the list's shape. */
export function toSummary(detail: DeviceDetailDto): DeviceSummaryDto {
  return {
    id: detail.id,
    integrationId: detail.integrationId,
    parentDeviceId: detail.parentDeviceId,
    name: detail.name,
    deviceClass: detail.deviceClass,
    icon: detail.icon,
    roomId: detail.roomId,
    availability: detail.availability,
    isOrphaned: detail.isOrphaned,
    lastSeenAtUtc: detail.lastSeenAtUtc,
    endpoints: detail.endpoints,
    possibleDuplicateIds: detail.possibleDuplicateIds,
  }
}

/**
 * The home's devices (`GET /api/devices`), the capability catalogue, rooms and integrations, shared by the device pages
 * (and later the dashboard and the floor plan). Current values live in the realtime store, which the lists seed; the
 * lists reload when the hub reports `DevicesChanged`.
 */
export const useDevicesStore = defineStore('devices', () => {
  const realtime = useRealtimeStore()

  const devices = shallowRef<DeviceSummaryDto[]>([])
  const capabilities = shallowRef<ReadonlyMap<string, CapabilityDto>>(new Map())
  const rooms = shallowRef<RoomListItemDto[]>([])
  const integrations = shallowRef<IntegrationDto[]>([])
  const loaded = ref(false)
  const loading = ref(false)
  const error = ref<unknown>(null)
  let pending: Promise<void> | undefined
  let reloadTimer: ReturnType<typeof setTimeout> | undefined

  /** Seeds the realtime values from a device's attributes (newer live values win). */
  function seedValues(list: readonly Pick<DeviceSummaryDto, 'endpoints'>[]): void {
    realtime.seed(
      list.flatMap((device) =>
        device.endpoints.flatMap((endpoint) =>
          endpoint.capabilities.flatMap((capability) => capability.attributes),
        ),
      ),
    )
  }

  /** Loads everything once (`force` reloads; the capability catalogue is fetched only once). */
  async function load(force = false): Promise<void> {
    if (loaded.value && !force) return
    pending ??= (async () => {
      loading.value = true
      error.value = null
      try {
        const [deviceList, catalogue, roomList, integrationList] = await Promise.all([
          listDevices().then(unwrap),
          capabilities.value.size > 0 ? Promise.resolve(null) : listCapabilities().then(unwrap),
          listRooms().then(unwrap),
          listIntegrations().then(unwrap),
        ])
        if (catalogue)
          capabilities.value = new Map(catalogue.map((capability) => [capability.id, capability]))
        seedValues(deviceList)
        devices.value = deviceList
        rooms.value = roomList
        integrations.value = integrationList
        loaded.value = true
      } catch (failure) {
        error.value = failure
      } finally {
        loading.value = false
        pending = undefined
      }
    })()
    await pending
  }

  function scheduleReload(): void {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(() => {
      reloadTimer = undefined
      void load(true)
    }, RELOAD_DEBOUNCE_MS)
  }

  // Devices added, removed or renamed anywhere (another tab, an integration's inventory): reload the lists.
  watch(
    () => realtime.devicesVersion,
    () => {
      if (loaded.value) scheduleReload()
    },
  )

  const roomsById = computed(() => new Map(rooms.value.map((room) => [room.id, room])))
  const integrationsById = computed(() => new Map(integrations.value.map((entry) => [entry.id, entry])))
  const devicesById = computed(() => new Map(devices.value.map((device) => [device.id, device])))

  /** Every endpoint as a grid entry, in the server's order. */
  const entries = computed<EndpointEntry[]>(() =>
    devices.value.flatMap((device) =>
      device.endpoints.map((endpoint) => ({
        device,
        endpoint,
        roomId: endpoint.effectiveRoomId ?? device.roomId,
      })),
    ),
  )

  /** `Online`, `Offline` or `Unknown`, live. */
  function availabilityOf(device: Pick<DeviceSummaryDto, 'id' | 'availability'>): string {
    return realtime.availability.get(device.id) ?? device.availability
  }

  /** An integration's status, live. */
  function integrationStatusOf(integration: Pick<IntegrationDto, 'id' | 'status' | 'statusMessage'>): {
    status: string
    message: string | null
  } {
    const live = realtime.integrationStatuses.get(integration.id)
    return live
      ? { status: live.status, message: live.message }
      : { status: integration.status, message: integration.statusMessage }
  }

  /** The definition of an endpoint capability: the connector's for `generic`, else the catalogue's. */
  function definitionOf(capability: Pick<EndpointCapabilityDto, 'capabilityId' | 'genericDefinition'>) {
    return capability.genericDefinition ?? capabilities.value.get(capability.capabilityId)
  }

  /** Puts a device fetched or saved elsewhere (the detail page) into the list. */
  function upsertDevice(detail: DeviceDetailDto): void {
    seedValues([detail])
    const summary = toSummary(detail)
    const index = devices.value.findIndex((device) => device.id === detail.id)
    devices.value =
      index < 0
        ? [...devices.value, summary]
        : devices.value.map((device, position) => (position === index ? summary : device))
  }

  /** Puts a saved endpoint (favorite, name) into its device. */
  function upsertEndpoint(saved: EndpointDto): void {
    devices.value = devices.value.map((device) =>
      device.endpoints.some((endpoint) => endpoint.id === saved.id)
        ? {
            ...device,
            endpoints: device.endpoints.map((endpoint) => (endpoint.id === saved.id ? saved : endpoint)),
          }
        : device,
    )
  }

  function setIntegrations(list: IntegrationDto[]): void {
    integrations.value = list
  }

  /** Forgets everything (sign-out). */
  function reset(): void {
    clearTimeout(reloadTimer)
    devices.value = []
    rooms.value = []
    integrations.value = []
    loaded.value = false
    error.value = null
  }

  return {
    devices,
    capabilities,
    rooms,
    integrations,
    loaded,
    loading,
    error,
    entries,
    roomsById,
    integrationsById,
    devicesById,
    load,
    scheduleReload,
    seedValues,
    availabilityOf,
    integrationStatusOf,
    definitionOf,
    upsertDevice,
    upsertEndpoint,
    setIntegrations,
    reset,
  }
})
