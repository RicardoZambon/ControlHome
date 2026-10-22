import type {
  AttributeDefinitionDto,
  AttributeDto,
  CapabilityDto,
  EndpointCapabilityDto,
  EndpointDto,
  ValueSchema,
} from '@controlhome/api-client'
import type { SeriesKind } from '../../components/charts/chartOptions'

/**
 * What the device page's History tab charts: every recorded attribute of the device's (non-retired) capabilities that
 * has a value kind a chart can draw, and the endpoints that use energy (bars). Energy counters are left out of the
 * line charts: the bars show what they count.
 */

export interface RecordedAttribute {
  endpoint: EndpointDto
  capability: EndpointCapabilityDto
  attribute: AttributeDto
  definition: AttributeDefinitionDto
  kind: SeriesKind
}

/** How a value of this schema is charted, or `null` when it is not (JSON, momentary events, flags). */
export function seriesKind(schema: ValueSchema): SeriesKind | null {
  switch (schema.type) {
    case 'number':
      return 'number'
    case 'bool':
      return 'bool'
    case 'enum':
    case 'string':
      return 'enum'
    default:
      return null
  }
}

export function recordedAttributes(
  endpoints: readonly EndpointDto[],
  definitionOf: (capability: EndpointCapabilityDto) => CapabilityDto | undefined,
): RecordedAttribute[] {
  const result: RecordedAttribute[] = []
  for (const endpoint of endpoints) {
    for (const capability of endpoint.capabilities) {
      if (capability.retired) continue
      const definition = definitionOf(capability)
      if (!definition) continue
      for (const attributeDefinition of definition.attributes) {
        if (!attributeDefinition.recorded) continue
        if (
          attributeDefinition.stateClass === 'total' ||
          attributeDefinition.stateClass === 'total_increasing'
        )
          continue
        const kind = seriesKind(attributeDefinition.schema)
        const attribute = capability.attributes.find((entry) => entry.key === attributeDefinition.key)
        if (!kind || !attribute) continue
        result.push({ endpoint, capability, attribute, definition: attributeDefinition, kind })
      }
    }
  }
  return result
}

/** Endpoints with an energy meter or power readings (their energy is in `EnergyHourly`). */
export function energyEndpoints(endpoints: readonly EndpointDto[]): EndpointDto[] {
  return endpoints.filter((endpoint) =>
    endpoint.capabilities.some(
      (capability) =>
        !capability.retired && (capability.capabilityId === 'energy' || capability.capabilityId === 'power'),
    ),
  )
}

const ZERO_BASED_UNITS = new Set(['W', 'kW', 'Wh', 'kWh', '%'])

/** Amounts (watts, energy, percentages) are charted from zero; other readings (temperatures) fit their values. */
export function zeroBasedUnit(unit: string | null | undefined): boolean {
  return unit !== null && unit !== undefined && ZERO_BASED_UNITS.has(unit)
}

/** The fill of an on/off timeline's "on" stretches: what being on means for the capability. */
export function timelineClass(capabilityId: string): string {
  switch (capabilityId) {
    case 'contact':
      return 'bg-state-warn'
    case 'smoke':
    case 'waterLeak':
      return 'bg-state-danger'
    case 'motion':
    case 'occupancy':
    case 'presence':
      return 'bg-state-info'
    default:
      return 'bg-state-on'
  }
}

/** The design token a chart of this attribute is drawn in. */
export function chartToken(capabilityId: string, attributeKey: string): string {
  if (capabilityId === 'power' || capabilityId === 'energy') return 'state-energy'
  if (/temperature/i.test(attributeKey)) return 'state-warn'
  if (capabilityId === 'humidity' || capabilityId === 'waterLeak') return 'state-info'
  if (capabilityId === 'battery') return 'state-on'
  if (capabilityId === 'smoke' || capabilityId === 'alarmPanel') return 'state-danger'
  return 'primary'
}
