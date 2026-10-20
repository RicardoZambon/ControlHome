import type { EndpointDto, StateValue } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import { createAppI18n } from '../../i18n'
import { basicHome, bool, num, str } from '../../test/devices'
import { airQualityLevel, summarizeEndpoint } from './deviceState'

const i18n = createAppI18n({ locale: 'en' })
const t = (key: string, named?: Record<string, unknown>) => i18n.global.t(key, named ?? {})
const te = (key: string) => i18n.global.te(key)

/** An endpoint with these capabilities, and its values by `capability.attribute`. */
function summary(
  capabilities: string[],
  values: Record<string, StateValue>,
  context: { availability?: string; deviceClass?: string; locale?: string } = {},
) {
  const endpoint = {
    capabilities: capabilities.map((capabilityId) => ({ capabilityId, retired: false })),
  } as unknown as EndpointDto
  return summarizeEndpoint(endpoint, (capability, key) => values[`${capability}.${key}`], {
    availability: context.availability ?? 'Online',
    deviceClass: context.deviceClass,
    locale: context.locale ?? 'en',
    t,
    te,
  })
}

describe('summarizeEndpoint', () => {
  it('a light: on with its brightness, off, unknown', () => {
    expect(summary(['switch', 'level'], { 'switch.on': bool(true), 'level.level': num(80) })).toEqual({
      tone: 'on',
      label: 'On',
      details: ['80 %'],
      active: true,
      capabilityId: 'switch',
    })
    expect(summary(['switch', 'level'], { 'switch.on': bool(false), 'level.level': num(80) })).toMatchObject({
      tone: 'off',
      label: 'Off',
      details: [],
      active: false,
    })
    expect(summary(['switch'], {})).toMatchObject({ tone: 'neutral', label: 'Unknown' })
  })

  it('a plug: its power draw', () => {
    expect(
      summary(['switch', 'power', 'energy'], { 'switch.on': bool(true), 'power.power': num(37.4) }),
    ).toMatchObject({
      label: 'On',
      details: ['37 W'],
    })
  })

  it('an AC: off, or what it is doing with the room temperature', () => {
    const cooling = {
      'switch.on': bool(true),
      'thermostat.mode': str('cool'),
      'thermostat.targetTemperature': num(22.5),
      'thermostat.currentTemperature': num(26.4),
    }
    expect(summary(['switch', 'thermostat'], cooling)).toEqual({
      tone: 'on',
      label: 'Cooling to 22.5 °C',
      details: ['26.4 °C'],
      active: true,
      capabilityId: 'thermostat',
    })
    expect(summary(['switch', 'thermostat'], cooling, { locale: 'pt-BR' }).details).toEqual(['26,4 °C'])
    expect(summary(['switch', 'thermostat'], { ...cooling, 'switch.on': bool(false) })).toMatchObject({
      tone: 'off',
      label: 'Off',
    })
    expect(summary(['thermostat'], { ...cooling, 'thermostat.mode': str('fan') })).toMatchObject({
      label: 'Fan',
    })
  })

  it('a lock: locked is calm, unlocked a warning, jammed a danger', () => {
    expect(summary(['lock'], { 'lock.lockState': str('locked') })).toMatchObject({
      tone: 'on',
      label: 'Locked',
    })
    expect(summary(['lock'], { 'lock.lockState': str('unlocked') })).toMatchObject({
      tone: 'warn',
      label: 'Unlocked',
    })
    expect(summary(['lock'], { 'lock.lockState': str('jammed') })).toMatchObject({ tone: 'danger' })
    expect(summary(['lock'], {})).toMatchObject({ tone: 'neutral', label: 'Unknown' })
  })

  it('a cover: moving, part open, open (a warning on a gate), closed', () => {
    expect(summary(['cover'], { 'cover.coverState': str('opening') })).toMatchObject({
      tone: 'info',
      label: 'Opening…',
    })
    expect(
      summary(['cover'], { 'cover.position': num(40), 'cover.coverState': str('stopped') }),
    ).toMatchObject({
      tone: 'on',
      label: '40 % open',
    })
    expect(summary(['cover'], { 'cover.coverState': str('open') }, { deviceClass: 'gate' })).toMatchObject({
      tone: 'warn',
      label: 'Open',
    })
    expect(summary(['cover'], { 'cover.position': num(0) })).toMatchObject({ tone: 'off', label: 'Closed' })
  })

  it('sensors in words, alarms first', () => {
    expect(
      summary(['contact', 'battery'], { 'contact.open': bool(true), 'battery.battery': num(88) }),
    ).toMatchObject({
      tone: 'warn',
      label: 'Open',
      details: [],
    })
    expect(
      summary(['contact', 'battery'], { 'contact.open': bool(false), 'battery.battery': num(9) }),
    ).toMatchObject({
      tone: 'off',
      label: 'Closed',
      details: ['Battery 9 %'],
    })
    expect(summary(['motion'], { 'motion.motion': bool(true) })).toMatchObject({
      tone: 'info',
      label: 'Motion',
    })
    expect(summary(['presence'], { 'presence.present': bool(false) })).toMatchObject({ label: 'Away' })
    expect(
      summary(['smoke', 'temperature'], { 'smoke.smoke': bool(true), 'temperature.temperature': num(21) }),
    ).toMatchObject({ tone: 'danger', label: 'Smoke detected', active: true })
    expect(summary(['waterLeak'], { 'waterLeak.leak': bool(true) })).toMatchObject({ tone: 'danger' })
    expect(
      summary(['temperature', 'humidity'], {
        'temperature.temperature': num(21),
        'humidity.humidity': num(55),
      }),
    ).toMatchObject({ tone: 'neutral', label: '21 °C', details: ['55 %'] })
    expect(summary(['energy'], { 'energy.energy': num(12_500) })).toMatchObject({ label: '12.50 kWh' })
    expect(summary(['button'], { 'button.press': str('double') })).toMatchObject({ label: 'Double press' })
    expect(summary(['button'], {})).toMatchObject({ label: 'Not pressed yet' })
  })

  it('an appliance: running with the time left', () => {
    expect(
      summary(['applianceCycle'], {
        'applianceCycle.machineState': str('running'),
        'applianceCycle.remainingMinutes': num(42),
      }),
    ).toMatchObject({ tone: 'on', label: 'Running', details: ['42 min left'], active: true })
    expect(summary(['applianceCycle'], { 'applianceCycle.machineState': str('error') })).toMatchObject({
      tone: 'danger',
    })
  })

  it('offline wins over every value', () => {
    expect(summary(['switch'], { 'switch.on': bool(true) }, { availability: 'Offline' })).toMatchObject({
      tone: 'offline',
      label: 'Offline',
      active: false,
    })
  })

  it('works on the fixtures (the basic Simulated home)', () => {
    const [light] = basicHome()
    const values = new Map(
      light!.endpoints[0]!.capabilities.flatMap((capability) =>
        capability.attributes.map((attribute) => [
          `${capability.capabilityId}.${attribute.key}`,
          attribute.value,
        ]),
      ),
    )
    expect(
      summarizeEndpoint(light!.endpoints[0]!, (capability, key) => values.get(`${capability}.${key}`), {
        availability: 'Online',
        locale: 'en',
        t,
        te,
      }).label,
    ).toBe('On')
  })
})

describe('airQualityLevel', () => {
  it('is the worst of the three bands', () => {
    expect(airQualityLevel({ pm25: 5, co2: 600, voc: 100 })).toBe('good')
    expect(airQualityLevel({ pm25: 5, co2: 1200 })).toBe('moderate')
    expect(airQualityLevel({ pm25: 40, co2: 600 })).toBe('poor')
    expect(airQualityLevel({})).toBeUndefined()
  })
})
