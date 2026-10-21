import type { CapabilityDto, EndpointCapabilityDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import { CATALOGUE, basicHome } from '../../test/devices'
import {
  chartToken,
  energyEndpoints,
  recordedAttributes,
  seriesKind,
  timelineClass,
  zeroBasedUnit,
} from './history'

/** The fixture catalogue with history flags as the server has them. */
const RECORDED: Record<string, string[]> = {
  switch: ['on'],
  thermostat: ['mode', 'targetTemperature', 'currentTemperature'],
  power: ['power'],
  energy: ['energy'],
  temperature: ['temperature'],
  contact: ['open'],
}

const catalogue = new Map<string, CapabilityDto>(
  CATALOGUE.map((capability) => [
    capability.id,
    {
      ...capability,
      attributes: capability.attributes.map((attribute) => ({
        ...attribute,
        recorded: RECORDED[capability.id]?.includes(attribute.key) ?? false,
        stateClass:
          capability.id === 'energy'
            ? 'total_increasing'
            : capability.id === 'power'
              ? 'measurement'
              : attribute.stateClass,
      })),
    },
  ]),
)
const definitionOf = (capability: EndpointCapabilityDto) => catalogue.get(capability.capabilityId)

describe('device history', () => {
  it('charts the recorded attributes that have a value, never the energy counter', () => {
    const [, plug, ac] = basicHome()
    expect(
      recordedAttributes(plug!.endpoints, definitionOf).map(
        (entry) => `${entry.capability.capabilityId}.${entry.attribute.key}`,
      ),
    ).toEqual(['switch.on', 'power.power'])
    const climate = recordedAttributes(ac!.endpoints, definitionOf)
    expect(climate.map((entry) => [entry.attribute.key, entry.kind])).toEqual([
      ['on', 'bool'],
      ['mode', 'enum'],
      ['targetTemperature', 'number'],
      ['currentTemperature', 'number'],
      ['temperature', 'number'],
    ])
  })

  it('skips retired capabilities', () => {
    const [, plug] = basicHome()
    const retired = plug!.endpoints.map((endpoint) => ({
      ...endpoint,
      capabilities: endpoint.capabilities.map((capability) => ({ ...capability, retired: true })),
    }))
    expect(recordedAttributes(retired, definitionOf)).toEqual([])
    expect(energyEndpoints(retired)).toEqual([])
  })

  it('finds the endpoints that use energy', () => {
    const devices = basicHome()
    expect(
      devices.flatMap((device) => energyEndpoints(device.endpoints)).map((endpoint) => endpoint.id),
    ).toEqual(['e-plug'])
  })

  it('knows which schemas can be charted', () => {
    expect(seriesKind({ type: 'number' })).toBe('number')
    expect(seriesKind({ type: 'bool' })).toBe('bool')
    expect(seriesKind({ type: 'enum', values: ['a'] })).toBe('enum')
    expect(seriesKind({ type: 'json' })).toBeNull()
    expect(seriesKind({ type: 'momentary' })).toBeNull()
  })

  it('draws each kind of reading in its own color', () => {
    expect(chartToken('power', 'power')).toBe('state-energy')
    expect(chartToken('thermostat', 'currentTemperature')).toBe('state-warn')
    expect(chartToken('humidity', 'humidity')).toBe('state-info')
    expect(chartToken('switch', 'on')).toBe('primary')
  })
})

describe('chart scales and timelines', () => {
  it('starts amounts at zero and fits the rest', () => {
    for (const unit of ['W', 'kW', 'Wh', 'kWh', '%']) expect(zeroBasedUnit(unit)).toBe(true)
    for (const unit of ['°C', 'ppm', null, undefined]) expect(zeroBasedUnit(unit)).toBe(false)
  })

  it('fills an on/off timeline in the color of what "on" means', () => {
    expect(timelineClass('switch')).toBe('bg-state-on')
    expect(timelineClass('contact')).toBe('bg-state-warn')
    expect(timelineClass('smoke')).toBe('bg-state-danger')
    expect(timelineClass('motion')).toBe('bg-state-info')
  })
})
