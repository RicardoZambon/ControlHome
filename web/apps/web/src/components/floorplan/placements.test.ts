import type { DeviceSummaryDto, PlacementDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import { basicHome } from '../../test/devices'
import { MARKER_RADIUS_PX, markerRadiusPx } from './markerTone'
import {
  clusterMarkers,
  hitMarker,
  matchesSearch,
  normalizeText,
  planMarkers,
  targetKey,
  targetName,
  targetOf,
  unplacedGroups,
  upsertPlacement,
} from './placements'

function placement(
  id: number,
  target: { endpointId?: string; deviceId?: string },
  x = 100,
  y = 100,
): PlacementDto {
  return {
    id,
    floorId: 1,
    endpointId: target.endpointId ?? null,
    deviceId: target.deviceId ?? null,
    xCm: x,
    yCm: y,
    roomId: null,
  }
}

/** A 3-gang wall switch: three endpoints (l1–l3), the third one hidden. */
function wallSwitch(): DeviceSummaryDto {
  const light = basicHome()[0]!
  const channel = (key: string, hidden = false) => ({
    ...light.endpoints[0]!,
    id: `e-switch-${key}`,
    key,
    hidden,
  })
  return {
    ...light,
    id: 'd-switch',
    name: 'Wall switch',
    endpoints: [channel('l1'), channel('l2'), channel('l3', true)],
  }
}

describe('planMarkers', () => {
  it('resolves endpoint and device placements, skipping unknown targets', () => {
    const devices = [...basicHome(), wallSwitch()]
    const markers = planMarkers(
      [
        placement(1, { endpointId: 'e-light' }),
        placement(2, { deviceId: 'd-switch' }),
        placement(3, { endpointId: 'e-gone' }),
        placement(4, { endpointId: 'e-switch-l2' }),
      ],
      devices,
    )
    expect(markers.map((marker) => marker.name)).toEqual([
      'Living room light',
      'Wall switch',
      'Wall switch · l2',
    ])
    expect(markers[0]!.endpoint?.id).toBe('e-light')
    // A device placed whole stands for its visible endpoints.
    expect(markers[1]!.endpoint).toBeNull()
    expect(markers[1]!.endpoints.map((endpoint) => endpoint.key)).toEqual(['l1', 'l2'])
  })

  it('names a control of a device with several by both ("Wall switch · Ceiling")', () => {
    const device = wallSwitch()
    device.endpoints = device.endpoints.map((endpoint) =>
      endpoint.key === 'l1' ? { ...endpoint, name: 'Ceiling' } : endpoint,
    )
    const [marker] = planMarkers([placement(1, { endpointId: 'e-switch-l1' })], [device])
    expect(marker!.name).toBe('Wall switch · Ceiling')
  })
})

describe('clusterMarkers', () => {
  const at = (id: number, x: number, y: number, roomId: number | null = 1) => ({
    placement: { ...placement(id, { endpointId: `e${id}` }, x, y), roomId },
  })

  it('groups the markers of a room that would overlap on screen, one by one otherwise', () => {
    const markers = [at(1, 100, 100), at(2, 160, 100), at(3, 600, 100), at(4, 120, 110, 2)]
    // 0.2 px per cm: markers 60 cm apart are 12 px apart, closer than two 9 px markers.
    const clusters = clusterMarkers(markers, 0.2, 9)
    expect(clusters.map((cluster) => cluster.members.map((member) => member.placement.id))).toEqual([
      [1, 2],
      [3],
      [4],
    ])
    expect(clusters[0]!.center).toEqual({ x: 130, y: 100 })
    // Zoomed in, they part.
    expect(clusterMarkers(markers, 1, 9)).toHaveLength(4)
  })
})

describe('markerRadiusPx', () => {
  it('follows the zoom between a small dot and the full marker', () => {
    expect(markerRadiusPx(1, false)).toBe(MARKER_RADIUS_PX.fine)
    expect(markerRadiusPx(1, true)).toBe(MARKER_RADIUS_PX.coarse)
    expect(markerRadiusPx(0.1, false)).toBe(MARKER_RADIUS_PX.min)
    const between = markerRadiusPx(0.45, false)
    expect(between).toBeGreaterThan(MARKER_RADIUS_PX.min)
    expect(between).toBeLessThan(MARKER_RADIUS_PX.fine)
  })
})

describe('hitMarker', () => {
  const markers = [
    { placement: placement(1, { endpointId: 'a' }, 100, 100) },
    { placement: placement(2, { endpointId: 'b' }, 130, 100) },
    { placement: placement(3, { endpointId: 'c' }, 130, 100) },
  ]

  it('finds the closest marker within the radius', () => {
    expect(hitMarker(markers, { x: 108, y: 100 }, 20)?.placement.id).toBe(1)
    expect(hitMarker(markers, { x: 400, y: 400 }, 20)).toBeUndefined()
  })

  it('prefers the marker drawn last (on top) on a tie', () => {
    expect(hitMarker(markers, { x: 130, y: 100 }, 20)?.placement.id).toBe(3)
  })

  it('counts the edge of the radius as a hit', () => {
    expect(hitMarker(markers, { x: 100, y: 120 }, 20)?.placement.id).toBe(1)
  })
})

describe('unplacedGroups', () => {
  it('lists every reported device with its visible, unplaced endpoints', () => {
    const groups = unplacedGroups([...basicHome(), wallSwitch()], [])
    expect(groups.map((group) => group.device.id)).toEqual([
      'd-light',
      'd-plug',
      'd-ac',
      'd-contact',
      'd-lock',
      'd-switch',
    ])
    const light = groups[0]!
    expect(light.whole).toBe(false)
    expect(light.primary).toEqual({ endpointId: 'e-light' })
    const wall = groups[5]!
    expect(wall.whole).toBe(true)
    expect(wall.primary).toEqual({ deviceId: 'd-switch' })
    expect(wall.endpoints.map((endpoint) => endpoint.key)).toEqual(['l1', 'l2'])
  })

  it('leaves out placed endpoints and devices placed whole', () => {
    const groups = unplacedGroups(
      [...basicHome(), wallSwitch()],
      [placement(1, { endpointId: 'e-light' }), placement(2, { deviceId: 'd-plug' })],
    )
    expect(groups.map((group) => group.device.id)).not.toContain('d-light')
    expect(groups.map((group) => group.device.id)).not.toContain('d-plug')
  })

  it('once a control is placed, places the others one by one (never the device whole)', () => {
    const groups = unplacedGroups([wallSwitch()], [placement(1, { endpointId: 'e-switch-l1' })])
    expect(groups).toHaveLength(1)
    expect(groups[0]!.whole).toBe(false)
    expect(groups[0]!.primary).toEqual({ endpointId: 'e-switch-l2' })
    expect(groups[0]!.endpoints.map((endpoint) => endpoint.key)).toEqual(['l2'])
  })

  it('leaves out orphaned devices and devices whose endpoints are all hidden', () => {
    const [light, plug] = basicHome()
    const groups = unplacedGroups(
      [
        { ...light!, isOrphaned: true },
        { ...plug!, endpoints: plug!.endpoints.map((endpoint) => ({ ...endpoint, hidden: true })) },
      ],
      [],
    )
    expect(groups).toEqual([])
  })
})

describe('targets', () => {
  it('keys and names targets', () => {
    const devices = [...basicHome(), wallSwitch()]
    expect(targetKey({ endpointId: 'x' })).toBe('e:x')
    expect(targetKey({ deviceId: 'y' })).toBe('d:y')
    expect(targetOf(placement(1, { deviceId: 'd-switch' }))).toEqual({ deviceId: 'd-switch' })
    expect(targetName({ endpointId: 'e-switch-l1' }, devices)).toBe('Wall switch · l1')
    expect(targetName({ deviceId: 'd-ac' }, devices)).toBe('Bedroom air conditioner')
    expect(targetName({ deviceId: 'nope' }, devices)).toBe('')
  })

  it('upserts a saved placement by id or by target', () => {
    const list = [placement(1, { endpointId: 'a' }), placement(2, { deviceId: 'b' })]
    const moved = { ...placement(2, { deviceId: 'b' }), xCm: 500 }
    expect(upsertPlacement(list, moved).map((entry) => entry.xCm)).toEqual([100, 500])
    const added = upsertPlacement(list, placement(3, { endpointId: 'c' }))
    expect(added.map((entry) => entry.id)).toEqual([1, 2, 3])
    // A target placed again (another floor) keeps one entry.
    const again = upsertPlacement(list, { ...placement(9, { endpointId: 'a' }), floorId: 2 })
    expect(again.map((entry) => entry.id)).toEqual([9, 2])
  })
})

describe('search', () => {
  it('matches device and endpoint names without case or accents', () => {
    expect(normalizeText('  Escritório ')).toBe('escritorio')
    const [group] = unplacedGroups([wallSwitch()], [])
    expect(matchesSearch(group!, 'WALL')).toBe(true)
    expect(matchesSearch(group!, 'l2')).toBe(true)
    expect(matchesSearch(group!, 'kitchen')).toBe(false)
    expect(matchesSearch(group!, '')).toBe(true)
  })
})
