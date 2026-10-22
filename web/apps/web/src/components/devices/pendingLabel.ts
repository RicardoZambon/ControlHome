import type { PendingCommand } from '../../stores/realtime'
import { formatMeasurement, valueLabel, type HasTranslation, type Translate } from './format'

/** The arguments of setters, with the unit their value is shown in. */
const SETTERS: Record<string, { key: string; unit: string }> = {
  setLevel: { key: 'level', unit: '%' },
  setTargetTemperature: { key: 'targetTemperature', unit: '°C' },
  setPosition: { key: 'position', unit: '%' },
  setVolume: { key: 'volume', unit: '%' },
}

/** Enum setters: "Switching to Cool…". */
const CHOICES: Record<string, string> = { setMode: 'mode', setFanMode: 'fanMode' }

const VERBS: Record<string, string> = {
  turnOn: 'device.pending.turningOn',
  powerOn: 'device.pending.turningOn',
  turnOff: 'device.pending.turningOff',
  powerOff: 'device.pending.turningOff',
  toggle: 'device.pending.switching',
  lock: 'device.lock.locking',
  unlock: 'device.lock.unlocking',
  open: 'device.pending.opening',
  close: 'device.pending.closing',
  stop: 'device.pending.stopping',
  setColor: 'device.pending.color',
  setColorTemperature: 'device.pending.color',
}

/**
 * What a control says while a command of this browser is on its way, in the command's own words: "Turning on…",
 * "Locking…", "Setting 22 °C…", "Switching to Cool…" (else "Updating…"). A safety-critical command still waiting for
 * the safety PIN has done nothing yet: "Waiting for your PIN".
 */
export function pendingLabel(
  pending: Pick<PendingCommand, 'command' | 'args' | 'awaitingPin'>,
  t: Translate,
  te: HasTranslation,
  locale: string,
): string {
  if (pending.awaitingPin) return t('device.pending.awaitingPin')
  const setter = SETTERS[pending.command]
  const value = setter ? pending.args?.[setter.key] : undefined
  if (setter && typeof value === 'number')
    return t('device.pending.setting', { value: formatMeasurement(value, setter.unit, locale) })
  const choice = CHOICES[pending.command]
  const option = choice ? pending.args?.[choice] : undefined
  if (choice && typeof option === 'string')
    return t('device.pending.choosing', { value: valueLabel(choice, option, t, te) })
  const verb = VERBS[pending.command]
  return verb ? t(verb) : t('device.state.pending')
}
