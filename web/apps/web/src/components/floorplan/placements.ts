import type { DeviceSummaryDto, EndpointDto, PlacementDto } from '@controlhome/api-client'
import { endpointName, fullEndpointName } from '../../stores/devices'
import type { Point } from './geometry'

/**
 * Devices on the floor plans (docs/architecture.md §3 "Placement"): a placement puts one endpoint (a control of a
 * device) or a whole device at a point of a floor. These helpers resolve placements to what the plan draws, find the
 * marker under a tap and list what is not on any plan yet. Pure, so every rule is unit-tested.
 */

/** What a placement points at: exactly one of the two. */
export type PlacementTarget =
  { endpointId: string; deviceId?: never } | { deviceId: string; endpointId?: never }

/** A placement with the device (and endpoint) it shows. */
export interface PlanMarker {
  placement: PlacementDto
  device: DeviceSummaryDto
  /** The placed endpoint, or `null` when the whole device is placed. */
  endpoint: EndpointDto | null
  /** The endpoints the marker stands for: the placed one, or every visible endpoint of a device placed whole. */
  endpoints: EndpointDto[]
  name: string
}

/** A stable key for a target (`e:<id>` or `d:<id>`). */
export function targetKey(target: PlacementTarget): string {
  return target.endpointId !== undefined ? `e:${target.endpointId}` : `d:${target.deviceId}`
}

/** The target of a placement. */
export function targetOf(
  placement: Pick<PlacementDto, 'endpointId' | 'deviceId'>,
): PlacementTarget | undefined {
  if (placement.endpointId) return { endpointId: placement.endpointId }
  if (placement.deviceId) return { deviceId: placement.deviceId }
  return undefined
}

function visibleEndpoints(device: DeviceSummaryDto): EndpointDto[] {
  const visible = device.endpoints.filter((endpoint) => !endpoint.hidden)
  return visible.length > 0 ? visible : device.endpoints
}

/**
 * The markers of `placements` (in their order, so later ones draw on top). Placements whose device is unknown (removed
 * meanwhile, or not loaded yet) are left out.
 */
export function planMarkers(
  placements: readonly PlacementDto[],
  devices: readonly DeviceSummaryDto[],
): PlanMarker[] {
  const devicesById = new Map(devices.map((device) => [device.id, device]))
  const endpoints = new Map<string, { device: DeviceSummaryDto; endpoint: EndpointDto }>()
  for (const device of devices)
    for (const endpoint of device.endpoints) endpoints.set(endpoint.id, { device, endpoint })

  const markers: PlanMarker[] = []
  for (const placement of placements) {
    if (placement.endpointId) {
      const entry = endpoints.get(placement.endpointId)
      if (!entry) continue
      markers.push({
        placement,
        device: entry.device,
        endpoint: entry.endpoint,
        endpoints: [entry.endpoint],
        name: fullEndpointName(entry.device, entry.endpoint),
      })
    } else if (placement.deviceId) {
      const device = devicesById.get(placement.deviceId)
      if (!device) continue
      markers.push({
        placement,
        device,
        endpoint: null,
        endpoints: visibleEndpoints(device),
        name: device.name,
      })
    }
  }
  return markers
}

/** Markers of one room close enough to overlap on screen, drawn as one bubble with their count. */
export interface MarkerCluster<T> {
  /** The members' mean position (cm). */
  center: Point
  members: T[]
}

/**
 * The markers as they are drawn at a zoom (`scale` px per cm, markers `radiusPx` big): those of the same room whose
 * circles would overlap (closer than two radii and a gap) are grouped — tapping the group zooms in until they part.
 * Order is kept (by each group's first member).
 */
export function clusterMarkers<T extends { placement: Pick<PlacementDto, 'xCm' | 'yCm' | 'roomId'> }>(
  markers: readonly T[],
  scale: number,
  radiusPx: number,
): MarkerCluster<T>[] {
  const reach = (2 * radiusPx + 4) / Math.max(scale, 1e-6)
  const clusters: (MarkerCluster<T> & { roomId: number | null })[] = []
  for (const marker of markers) {
    const { xCm: x, yCm: y, roomId } = marker.placement
    const near = clusters.find(
      (cluster) =>
        cluster.roomId === roomId &&
        cluster.members.some(
          (member) => Math.hypot(member.placement.xCm - x, member.placement.yCm - y) < reach,
        ),
    )
    if (near) {
      near.members.push(marker)
      const count = near.members.length
      near.center = {
        x: near.members.reduce((sum, member) => sum + member.placement.xCm, 0) / count,
        y: near.members.reduce((sum, member) => sum + member.placement.yCm, 0) / count,
      }
    } else {
      clusters.push({ center: { x, y }, members: [marker], roomId })
    }
  }
  return clusters.map(({ center, members }) => ({ center, members }))
}

/**
 * The marker under a point: the closest one whose centre is within `radiusCm` (a marker drawn later wins a tie, as it
 * is on top).
 */
export function hitMarker<T extends { placement: Pick<PlacementDto, 'xCm' | 'yCm'> }>(
  markers: readonly T[],
  point: Point,
  radiusCm: number,
): T | undefined {
  let best: T | undefined
  let bestDistance = Infinity
  for (const marker of markers) {
    const distance = Math.hypot(marker.placement.xCm - point.x, marker.placement.yCm - point.y)
    if (distance <= radiusCm && distance <= bestDistance) {
      best = marker
      bestDistance = distance
    }
  }
  return best
}

/** A device with what of it can still be placed. */
export interface UnplacedGroup {
  device: DeviceSummaryDto
  /** The whole device can be placed (a device with several controls, none of them placed yet). */
  whole: boolean
  /** Its endpoints that are on no plan. */
  endpoints: EndpointDto[]
  /** What one click places: the whole device while that is possible, else its first unplaced endpoint. */
  primary: PlacementTarget
}

/**
 * What is not on any plan yet, by device in the given order: devices still reported by their integration, visible
 * endpoints only. A single-endpoint device places its endpoint; a device with several controls places either as a whole
 * (until one of its controls is placed) or control by control. A device placed whole has nothing left to place.
 */
export function unplacedGroups(
  devices: readonly DeviceSummaryDto[],
  placements: readonly Pick<PlacementDto, 'endpointId' | 'deviceId'>[],
): UnplacedGroup[] {
  const placedEndpoints = new Set(placements.flatMap((placement) => placement.endpointId ?? []))
  const placedDevices = new Set(placements.flatMap((placement) => placement.deviceId ?? []))
  const groups: UnplacedGroup[] = []
  for (const device of devices) {
    if (device.isOrphaned || placedDevices.has(device.id)) continue
    const visible = device.endpoints.filter((endpoint) => !endpoint.hidden)
    if (visible.length === 0) continue
    const endpoints = visible.filter((endpoint) => !placedEndpoints.has(endpoint.id))
    if (endpoints.length === 0) continue
    const whole = visible.length > 1 && endpoints.length === visible.length
    groups.push({
      device,
      whole,
      endpoints,
      primary: whole ? { deviceId: device.id } : { endpointId: endpoints[0]!.id },
    })
  }
  return groups
}

/** The name a target is shown with. */
export function targetName(target: PlacementTarget, devices: readonly DeviceSummaryDto[]): string {
  for (const device of devices) {
    if (target.deviceId === device.id) return device.name
    const endpoint = device.endpoints.find((entry) => entry.id === target.endpointId)
    if (endpoint) return endpointName(device, endpoint)
  }
  return ''
}

/** Whether a free-text search matches a device (its name, or one of its endpoints' names). */
export function matchesSearch(group: Pick<UnplacedGroup, 'device' | 'endpoints'>, query: string): boolean {
  const needle = normalizeText(query)
  if (!needle) return true
  if (normalizeText(group.device.name).includes(needle)) return true
  return group.endpoints.some((endpoint) =>
    normalizeText(endpointName(group.device, endpoint)).includes(needle),
  )
}

/** Lower case without accents ("Escritório" → "escritorio"), for searches and name matching. */
export function normalizeText(text: string): string {
  return text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLocaleLowerCase()
    .trim()
}

/** Replaces (or adds) a placement in a list, matched by id or by target (a move answers with the same placement). */
export function upsertPlacement(list: readonly PlacementDto[], saved: PlacementDto): PlacementDto[] {
  const savedKey = targetOf(saved)
  const index = list.findIndex(
    (entry) =>
      entry.id === saved.id ||
      (savedKey !== undefined &&
        targetOf(entry) !== undefined &&
        targetKey(targetOf(entry)!) === targetKey(savedKey)),
  )
  if (index < 0) return [...list, saved]
  return list.map((entry, position) => (position === index ? saved : entry))
}
