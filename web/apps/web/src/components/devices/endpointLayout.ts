import type { EndpointCapabilityDto, EndpointDto } from '@controlhome/api-client'
import { resolveControl } from './capabilities'

/** Controls that take the endpoint's power switch into their card's header (by registry order, the first wins). */
const POWER_HOSTS = new Set(['level', 'thermostat', 'color'])

export interface ControlEntry {
  capability: EndpointCapabilityDto
  /** Takes both columns of the control grid. */
  full: boolean
  /** The endpoint's `switch`, shown as a toggle in this card's header. */
  power?: EndpointCapabilityDto
}

export interface EndpointLayout {
  controls: ControlEntry[]
  readings: EndpointCapabilityDto[]
  /** The readings fill the free column next to a lone half-width control (a plug's power beside its use). */
  readingsBeside: boolean
}

/**
 * An endpoint's controls (cards, in the registry's order) and readings (sensor cards sized to their content), shared by
 * the device page and the floor plan's quick panel. Power folds into the main adjustment's header; a reading the main
 * control already shows (an AC's room temperature on its dial) is left out; a lone half-width control gets the readings
 * beside it, or else takes the whole row. Retired capabilities are left out.
 */
export function layoutOf(endpoint: Pick<EndpointDto, 'capabilities'>): EndpointLayout {
  const resolved = endpoint.capabilities
    .filter((capability) => !capability.retired)
    .map((capability) => ({ capability, control: resolveControl(capability.capabilityId) }))
    .sort((a, b) => a.control.options.order - b.control.options.order)
  const has = (id: string) => resolved.some((entry) => entry.capability.capabilityId === id)
  const power = resolved.find((entry) => entry.capability.capabilityId === 'switch')?.capability
  const host = power
    ? resolved.find((entry) => POWER_HOSTS.has(entry.capability.capabilityId))?.capability
    : undefined
  const thermostat = resolved.find((entry) => entry.capability.capabilityId === 'thermostat')?.capability
  const dialShowsRoom = thermostat?.attributes.some((attribute) => attribute.key === 'currentTemperature')

  const controls: ControlEntry[] = resolved
    .filter((entry) => entry.control.options.kind === 'control')
    .filter((entry) => !(host && entry.capability === power))
    .map((entry) => ({
      capability: entry.capability,
      full: entry.control.options.wide,
      power: entry.capability === host ? power : undefined,
    }))
  const readings = resolved
    .filter((entry) => entry.control.options.kind === 'reading')
    .filter(
      (entry) => !(dialShowsRoom && has('thermostat') && entry.capability.capabilityId === 'temperature'),
    )
    .map((entry) => entry.capability)
  const halves = controls.filter((entry) => !entry.full)
  const readingsBeside = readings.length > 0 && readings.length <= 4 && halves.length % 2 === 1
  if (!readingsBeside && halves.length % 2 === 1) halves[halves.length - 1]!.full = true
  return { controls, readings, readingsBeside }
}
