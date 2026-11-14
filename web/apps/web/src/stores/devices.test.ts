import { describe, expect, it } from 'vitest'
import { createAppI18n } from '../i18n'
import { basicHome, fridge, wallSwitch } from '../test/devices'
import { deviceTiles, endpointName, fullEndpointName, isRetiredEndpoint } from './devices'

describe('deviceTiles', () => {
  it('shows an appliance as one tile, its main endpoint standing for every part still reported', () => {
    const tiles = deviceTiles(fridge())
    expect(tiles).toHaveLength(1)
    expect(tiles[0]!.endpoint.key).toBe('main')
    expect(tiles[0]!.parts!.map((part) => part.key)).toEqual(['main', 'freezer', 'icemaker-02'])
    expect(tiles[0]!.roomId).toBe(11)
  })

  it('keeps a tile per channel of independent actuators, and one per single-endpoint device', () => {
    expect(deviceTiles(wallSwitch()).map((tile) => tile.endpoint.key)).toEqual(['l1', 'l2', 'l3'])
    expect(deviceTiles(wallSwitch()).every((tile) => tile.parts === undefined)).toBe(true)
    expect(basicHome().flatMap(deviceTiles)).toHaveLength(5)
  })

  it('treats an endpoint whose capabilities are all retired as gone', () => {
    const device = fridge()
    expect(isRetiredEndpoint(device.endpoints[3]!)).toBe(true)
    expect(isRetiredEndpoint(device.endpoints[0]!)).toBe(false)
  })
})

describe.each([
  ['en', 'Freezer', 'Ice maker 2'],
  ['pt-BR', 'Congelador', 'Máquina de gelo 2'],
] as const)('endpointName (%s)', (locale, freezer, iceMaker) => {
  it('names the parts of an appliance in the language, the main one by the device', () => {
    createAppI18n({ locale })
    const device = fridge()
    const [main, freezerEndpoint, iceMakerEndpoint, retired] = device.endpoints
    expect(endpointName(device, main!)).toBe('Kitchen fridge')
    // A stored name that is only the vendor's id ("freezer") is no name a person chose.
    expect(endpointName(device, freezerEndpoint!)).toBe(freezer)
    expect(endpointName(device, iceMakerEndpoint!)).toBe(iceMaker)
    // A key without a translation keeps the device's name with it.
    expect(endpointName(device, retired!)).toBe('Kitchen fridge · hca_main')
    expect(fullEndpointName(device, freezerEndpoint!)).toBe(`Kitchen fridge · ${freezer}`)
    expect(fullEndpointName(device, main!)).toBe('Kitchen fridge')
  })

  it('keeps the name a person gave', () => {
    createAppI18n({ locale })
    const device = fridge()
    expect(endpointName(device, { ...device.endpoints[1]!, name: 'Ice cream' })).toBe('Ice cream')
    const gang = wallSwitch()
    expect(endpointName(gang, gang.endpoints[0]!)).toBe('Ceiling')
    expect(endpointName(gang, gang.endpoints[1]!)).toBe('Wall switch · l2')
    expect(fullEndpointName(gang, gang.endpoints[0]!)).toBe('Wall switch · Ceiling')
  })
})
