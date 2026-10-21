import type { EndpointDto, StateValue } from '@controlhome/api-client'
import { asBool, asNumber, asString } from '../../lib/stateValue'
import { formatMeasurement, valueLabel, type HasTranslation, type Translate } from './format'

/**
 * The state of an endpoint in one word and one color, for tiles, badges and (later) the floor plan: alerts first
 * (smoke, leak), then the capability that best describes the device (lock, cover, climate, power, media, appliance,
 * sensors). Pure, so every rule is unit-tested.
 */

/** A device state color: the `state-*` tokens, plus `neutral` for plain readings. */
export type Tone = 'on' | 'off' | 'offline' | 'warn' | 'danger' | 'info' | 'energy' | 'neutral'

export interface EndpointSummary {
  tone: Tone
  /** The state in words ("On", "Locked", "23,4 °C"). */
  label: string
  /** Extra facts shown after it ("80 %", "120 W"). */
  details: string[]
  /** Something is running or lit: the tile glows. */
  active: boolean
  /** The capability that gave the state. */
  capabilityId?: string
}

/** Reads the current value of an attribute of the endpoint (`undefined` when unknown). */
export type StateReader = (capabilityId: string, attributeKey: string) => StateValue | undefined

export interface SummaryContext {
  /** `Online`, `Offline` or `Unknown`. */
  availability: string
  deviceClass?: string | null
  locale: string
  t: Translate
  te: HasTranslation
}

export type AirQualityLevel = 'good' | 'moderate' | 'poor'

/** Indoor air from PM2.5 (µg/m³), CO₂ (ppm) and VOC (ppb): the worst of the three (WHO / common indoor bands). */
export function airQualityLevel(values: {
  pm25?: number
  co2?: number
  voc?: number
}): AirQualityLevel | undefined {
  const levels: AirQualityLevel[] = []
  const band = (value: number | undefined, moderate: number, poor: number) => {
    if (value === undefined) return
    levels.push(value >= poor ? 'poor' : value >= moderate ? 'moderate' : 'good')
  }
  band(values.pm25, 15, 35)
  band(values.co2, 1000, 1500)
  band(values.voc, 220, 660)
  if (levels.length === 0) return undefined
  if (levels.includes('poor')) return 'poor'
  return levels.includes('moderate') ? 'moderate' : 'good'
}

export const LOW_BATTERY_PERCENT = 20

/** Whether an endpoint stands open — a lock unlocked, a door or window open — so its icon can show it open. */
export function standsOpen(capabilityIds: readonly string[], read: StateReader): boolean {
  if (capabilityIds.includes('lock')) return asString(read('lock', 'lockState')) === 'unlocked'
  if (capabilityIds.includes('contact')) return asBool(read('contact', 'open')) === true
  return false
}

const GATE_CLASSES = new Set(['gate', 'garage'])

export function summarizeEndpoint(
  endpoint: Pick<EndpointDto, 'capabilities'>,
  read: StateReader,
  context: SummaryContext,
): EndpointSummary {
  const { t, te, locale } = context
  const present = new Set(
    endpoint.capabilities
      .filter((capability) => !capability.retired)
      .map((capability) => capability.capabilityId),
  )
  const has = (id: string) => present.has(id)
  const bool = (id: string, key: string) => asBool(read(id, key))
  const number = (id: string, key: string) => asNumber(read(id, key))
  const text = (id: string, key: string) => asString(read(id, key))
  const measure = (value: number, unit: string) => formatMeasurement(value, unit, locale)
  const details: string[] = []
  const result = (tone: Tone, label: string, capabilityId?: string, active = false): EndpointSummary => ({
    tone,
    label,
    details,
    active,
    capabilityId,
  })

  if (has('battery')) {
    const battery = number('battery', 'battery')
    if (battery !== undefined && battery < LOW_BATTERY_PERCENT)
      details.push(t('device.reading.batteryLow', { value: measure(battery, '%') }))
  }

  if (context.availability === 'Offline') return result('offline', t('device.availability.Offline'))

  // Alerts win over everything else.
  if (has('smoke') && bool('smoke', 'smoke'))
    return result('danger', t('device.reading.smoke'), 'smoke', true)
  if (has('waterLeak') && bool('waterLeak', 'leak'))
    return result('danger', t('device.reading.leak'), 'waterLeak', true)

  if (has('lock')) {
    const state = text('lock', 'lockState') ?? 'unknown'
    const tone: Tone =
      state === 'locked' ? 'on' : state === 'unlocked' ? 'warn' : state === 'jammed' ? 'danger' : 'neutral'
    return result(tone, valueLabel('lockState', state, t, te), 'lock')
  }

  if (has('alarmPanel')) {
    const state = text('alarmPanel', 'armState') ?? 'unknown'
    const tone: Tone =
      state === 'triggered'
        ? 'danger'
        : state === 'arming'
          ? 'info'
          : state.startsWith('armed')
            ? 'on'
            : 'off'
    return result(tone, valueLabel('armState', state, t, te), 'alarmPanel', state === 'triggered')
  }

  if (has('cover')) {
    const state = text('cover', 'coverState')
    const position = number('cover', 'position')
    if (state === 'opening' || state === 'closing')
      return result('info', valueLabel('coverState', state, t, te), 'cover', true)
    const open = state === 'open' || (state !== 'closed' && position !== undefined && position > 0)
    if (open) {
      const label =
        position !== undefined && position > 0 && position < 100
          ? t('device.cover.percentOpen', { value: measure(position, '%') })
          : valueLabel('coverState', 'open', t, te)
      return result(GATE_CLASSES.has(context.deviceClass ?? '') ? 'warn' : 'on', label, 'cover', true)
    }
    if (state === 'closed' || position === 0)
      return result('off', valueLabel('coverState', 'closed', t, te), 'cover')
    return result('neutral', valueLabel('coverState', state ?? 'unknown', t, te), 'cover')
  }

  const powered = has('switch') ? bool('switch', 'on') : undefined

  if (has('thermostat')) {
    // One number per line, each one named: "Cooling to 24 °C" (the target) or "Off · Room 27.9 °C" (the room).
    const mode = text('thermostat', 'mode')
    const target = number('thermostat', 'targetTemperature')
    const current = number('thermostat', 'currentTemperature') ?? number('temperature', 'temperature')
    const room = () => {
      if (current !== undefined)
        details.push(t('device.thermostat.room', { temperature: measure(current, '°C') }))
    }
    if (powered === false || mode === 'off' || (mode === undefined && powered !== true)) {
      room()
      return result(
        mode === undefined && powered === undefined ? 'neutral' : 'off',
        t('device.state.off'),
        'thermostat',
      )
    }
    if (target !== undefined && (mode === 'heat' || mode === 'cool' || mode === 'auto'))
      return result(
        'on',
        t(`device.thermostat.summary.${mode}`, { temperature: measure(target, '°C') }),
        'thermostat',
        true,
      )
    room()
    const label =
      mode && te(`device.thermostat.action.${mode}`)
        ? t(`device.thermostat.action.${mode}`)
        : mode
          ? valueLabel('mode', mode, t, te)
          : t('device.state.on')
    return result('on', label, 'thermostat', true)
  }

  if (has('mediaPlayer')) {
    const power = bool('mediaPlayer', 'power') ?? powered
    if (power === false) return result('off', t('device.state.off'), 'mediaPlayer')
    const playback = text('mediaPlayer', 'playbackState')
    const source = text('mediaPlayer', 'app') ?? text('mediaPlayer', 'source')
    if (source) details.push(source)
    if (power === undefined) return result('neutral', t('device.state.unknown'), 'mediaPlayer')
    const label =
      playback && playback !== 'unknown' && playback !== 'idle'
        ? valueLabel('playbackState', playback, t, te)
        : t('device.state.on')
    return result('on', label, 'mediaPlayer', true)
  }

  if (has('switch')) {
    if (powered === true && has('level')) {
      const level = number('level', 'level')
      if (level !== undefined) details.unshift(measure(level, '%'))
    }
    if (has('power')) {
      const watts = number('power', 'power')
      if (watts !== undefined && (powered === true || watts > 0)) details.unshift(measure(watts, 'W'))
    }
    if (powered === undefined) return result('neutral', t('device.state.unknown'), 'switch')
    return powered
      ? result('on', t('device.state.on'), 'switch', true)
      : result('off', t('device.state.off'), 'switch')
  }

  if (has('applianceCycle')) {
    const state = text('applianceCycle', 'machineState') ?? 'idle'
    const remaining = number('applianceCycle', 'remainingMinutes')
    if ((state === 'running' || state === 'paused') && remaining !== undefined)
      details.push(t('device.appliance.remaining', { minutes: remaining }))
    const label = valueLabel('machineState', state, t, te)
    if (state === 'running') return result('on', label, 'applianceCycle', true)
    if (state === 'paused') return result('warn', label, 'applianceCycle')
    if (state === 'finished') return result('info', label, 'applianceCycle')
    if (state === 'error') return result('danger', label, 'applianceCycle')
    return result('off', label, 'applianceCycle')
  }

  if (has('doorOpener')) return result('neutral', t('device.doorOpener.ready'), 'doorOpener')

  if (has('contact')) {
    const open = bool('contact', 'open')
    if (open === undefined) return result('neutral', t('device.state.unknown'), 'contact')
    return open
      ? result('warn', t('device.reading.open'), 'contact')
      : result('off', t('device.reading.closed'), 'contact')
  }

  if (has('motion')) {
    const motion = bool('motion', 'motion')
    if (motion === undefined) return result('neutral', t('device.state.unknown'), 'motion')
    return motion
      ? result('info', t('device.reading.motion'), 'motion', true)
      : result('off', t('device.reading.noMotion'), 'motion')
  }

  if (has('occupancy')) {
    const occupied = bool('occupancy', 'occupied')
    if (occupied === undefined) return result('neutral', t('device.state.unknown'), 'occupancy')
    return occupied
      ? result('info', t('device.reading.occupied'), 'occupancy', true)
      : result('off', t('device.reading.clear'), 'occupancy')
  }

  if (has('presence')) {
    const home = bool('presence', 'present')
    if (home === undefined) return result('neutral', t('device.state.unknown'), 'presence')
    return home
      ? result('on', t('device.reading.home'), 'presence', true)
      : result('off', t('device.reading.away'), 'presence')
  }

  if (has('airQuality')) {
    const level = airQualityLevel({
      pm25: number('airQuality', 'pm25'),
      co2: number('airQuality', 'co2'),
      voc: number('airQuality', 'voc'),
    })
    const co2 = number('airQuality', 'co2')
    if (co2 !== undefined) details.push(measure(co2, 'ppm'))
    if (!level) return result('neutral', t('device.state.unknown'), 'airQuality')
    const tone: Tone = level === 'good' ? 'on' : level === 'moderate' ? 'warn' : 'danger'
    return result(tone, t(`device.airQuality.${level}`), 'airQuality')
  }

  if (has('temperature')) {
    const temperature = number('temperature', 'temperature')
    if (has('humidity')) {
      const humidity = number('humidity', 'humidity')
      if (humidity !== undefined) details.push(measure(humidity, '%'))
    }
    if (temperature !== undefined) return result('neutral', measure(temperature, '°C'), 'temperature')
  }

  if (has('humidity')) {
    const humidity = number('humidity', 'humidity')
    if (humidity !== undefined) return result('neutral', measure(humidity, '%'), 'humidity')
  }

  if (has('power')) {
    const watts = number('power', 'power')
    if (watts !== undefined)
      return result(watts > 0 ? 'energy' : 'off', measure(watts, 'W'), 'power', watts > 0)
  }

  if (has('energy')) {
    const energy = number('energy', 'energy')
    if (energy !== undefined) return result('neutral', measure(energy, 'Wh'), 'energy')
  }

  if (has('battery')) {
    const battery = number('battery', 'battery')
    if (battery !== undefined)
      return result(battery < LOW_BATTERY_PERCENT ? 'warn' : 'neutral', measure(battery, '%'), 'battery')
  }

  if (has('button')) {
    const press = text('button', 'press')
    return result('neutral', press ? valueLabel('press', press, t, te) : t('device.button.never'), 'button')
  }

  if (has('camera')) {
    const stream = bool('camera', 'streamAvailable')
    return result('neutral', t(stream ? 'device.camera.streamAvailable' : 'device.camera.noStream'), 'camera')
  }

  return result('neutral', t('device.noValue'))
}
