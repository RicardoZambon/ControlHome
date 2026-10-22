import { describe, expect, it } from 'vitest'
import { basicHome } from '../../test/devices'
import { replaceCandidates, suggestsReplace } from './replace'

describe('replaceCandidates', () => {
  it('offers the same hardware first, then the same kind, never itself or orphaned devices', () => {
    const [light, plug, ac, contact, lock] = basicHome()
    const newLight = { ...light!, id: 'd-light-2', name: 'New light', integrationId: 'int-2' }
    const twin = { ...plug!, id: 'd-plug-mqtt', name: 'Plug (MQTT)', possibleDuplicateIds: ['d-light'] }
    const gone = { ...contact!, id: 'd-gone', isOrphaned: true }
    const old = { ...light!, isOrphaned: true }
    const candidates = replaceCandidates(old, [old, ac!, lock!, newLight, twin, gone])
    expect(candidates.map((entry) => [entry.device.id, entry.reason])).toEqual([
      ['d-plug-mqtt', 'duplicate'],
      ['d-light-2', 'sameClass'],
      ['d-ac', 'other'],
      ['d-lock', 'other'],
    ])
  })

  it('counts a duplicate flagged on either side', () => {
    const [light, plug] = basicHome()
    const old = { ...light!, possibleDuplicateIds: ['d-plug'] }
    expect(replaceCandidates(old, [plug!])[0]!.reason).toBe('duplicate')
  })
})

describe('suggestsReplace', () => {
  it('suggests it for orphaned devices and likely duplicates', () => {
    const [light] = basicHome()
    expect(suggestsReplace(light!)).toBe(false)
    expect(suggestsReplace({ ...light!, isOrphaned: true })).toBe(true)
    expect(suggestsReplace({ ...light!, possibleDuplicateIds: ['x'] })).toBe(true)
  })
})
