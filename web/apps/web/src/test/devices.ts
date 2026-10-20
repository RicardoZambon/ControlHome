// Device fixtures for unit tests: a slice of the capability catalogue and the Simulated connector's basic home.
import type {
  AttributeDto,
  CapabilityDto,
  CommandDefinitionDto,
  DeviceSummaryDto,
  EndpointCapabilityDto,
  EndpointDto,
  StateValue,
  ValueSchema,
} from '@controlhome/api-client'

const PERCENT: ValueSchema = { type: 'number', min: 0, max: 100, step: 1, unit: '%', integer: true }

export function command(name: string, extra: Partial<CommandDefinitionDto> = {}): CommandDefinitionDto {
  return {
    name,
    arguments: [],
    confirmation: 'awaitState',
    timeoutMs: null,
    safetyCritical: false,
    safetyCriticalForDeviceClasses: [],
    failSafe: false,
    gateAttribute: null,
    ...extra,
  }
}

function attribute(
  key: string,
  schema: ValueSchema,
  extra: Partial<CapabilityDto['attributes'][number]> = {},
) {
  return {
    key,
    schema,
    unit: schema.type === 'number' ? (schema.unit ?? null) : null,
    stateClass: 'none',
    category: 'primary',
    recorded: false,
    writable: false,
    ...extra,
  }
}

const argument = (key: string, schema: ValueSchema, required = true) => ({
  key,
  schema,
  required,
  attribute: null,
})

/** The catalogue entries the fixtures use (same shapes as `GET /api/capabilities`). */
export const CATALOGUE: CapabilityDto[] = [
  {
    id: 'switch',
    version: 1,
    attributes: [attribute('on', { type: 'bool' }, { writable: true })],
    commands: [command('turnOn'), command('turnOff'), command('toggle')],
  },
  {
    id: 'level',
    version: 1,
    attributes: [attribute('level', PERCENT, { writable: true })],
    commands: [command('setLevel', { arguments: [argument('level', PERCENT)] })],
  },
  {
    id: 'thermostat',
    version: 1,
    attributes: [
      attribute(
        'mode',
        { type: 'enum', values: ['off', 'cool', 'heat', 'auto', 'dry', 'fan'] },
        { writable: true },
      ),
      attribute(
        'targetTemperature',
        { type: 'number', min: 5, max: 35, step: 0.5, unit: '°C' },
        { writable: true },
      ),
      attribute('currentTemperature', { type: 'number', unit: '°C' }),
      attribute(
        'fanMode',
        { type: 'enum', values: ['auto', 'quiet', 'low', 'medium', 'high', 'turbo'] },
        { writable: true },
      ),
    ],
    commands: [
      command('setMode', {
        arguments: [
          argument('mode', { type: 'enum', values: ['off', 'cool', 'heat', 'auto', 'dry', 'fan'] }),
        ],
      }),
      command('setTargetTemperature', {
        arguments: [
          argument('targetTemperature', { type: 'number', min: 5, max: 35, step: 0.5, unit: '°C' }),
        ],
      }),
      command('setFanMode', {
        arguments: [
          argument('fanMode', { type: 'enum', values: ['auto', 'quiet', 'low', 'medium', 'high', 'turbo'] }),
        ],
      }),
    ],
  },
  { id: 'power', version: 1, attributes: [attribute('power', { type: 'number', unit: 'W' })], commands: [] },
  {
    id: 'energy',
    version: 1,
    attributes: [attribute('energy', { type: 'number', min: 0, unit: 'Wh' })],
    commands: [],
  },
  {
    id: 'temperature',
    version: 1,
    attributes: [attribute('temperature', { type: 'number', unit: '°C' })],
    commands: [],
  },
  { id: 'contact', version: 1, attributes: [attribute('open', { type: 'bool' })], commands: [] },
  {
    id: 'battery',
    version: 1,
    attributes: [attribute('battery', PERCENT, { category: 'diagnostic' })],
    commands: [],
  },
  {
    id: 'lock',
    version: 1,
    attributes: [
      attribute('lockState', { type: 'enum', values: ['locked', 'unlocked', 'jammed', 'unknown'] }),
    ],
    commands: [command('lock', { failSafe: true }), command('unlock', { safetyCritical: true })],
  },
  {
    id: 'alarmPanel',
    version: 1,
    attributes: [
      attribute('armState', {
        type: 'enum',
        values: ['disarmed', 'armed_home', 'armed_away', 'armed_night', 'triggered', 'arming'],
      }),
    ],
    commands: [
      command('arm', { arguments: [argument('mode', { type: 'enum', values: ['home', 'away', 'night'] })] }),
      command('disarm', { safetyCritical: true }),
    ],
  },
]

export const bool = (value: boolean): StateValue => ({ kind: 'bool', b: value, n: null, s: null, j: null })
export const num = (value: number): StateValue => ({ kind: 'number', b: null, n: value, s: null, j: null })
export const str = (value: string): StateValue => ({ kind: 'string', b: null, n: null, s: value, j: null })

export const OBSERVED = '2026-10-12T10:00:00Z'

function attr(attributeId: number, key: string, value: StateValue): AttributeDto {
  return { attributeId, key, value, observedAtUtc: OBSERVED, lastChangedAtUtc: OBSERVED, restored: false }
}

function capability(
  id: number,
  capabilityId: string,
  attributes: AttributeDto[],
  constraints: unknown = null,
): EndpointCapabilityDto {
  return {
    id,
    capabilityId,
    capabilityVersion: 1,
    retired: false,
    constraints,
    genericDefinition: null,
    attributes,
  }
}

function endpoint(
  id: string,
  capabilities: EndpointCapabilityDto[],
  extra: Partial<EndpointDto> = {},
): EndpointDto {
  return {
    id,
    key: 'main',
    name: null,
    roomId: null,
    effectiveRoomId: null,
    icon: null,
    hidden: false,
    favorite: false,
    sortOrder: 0,
    capabilities,
    ...extra,
  }
}

function device(
  id: string,
  name: string,
  deviceClass: string,
  endpoints: EndpointDto[],
  extra: Partial<DeviceSummaryDto> = {},
): DeviceSummaryDto {
  return {
    id,
    integrationId: 'int-1',
    parentDeviceId: null,
    name,
    deviceClass,
    icon: null,
    roomId: null,
    availability: 'Online',
    isOrphaned: false,
    lastSeenAtUtc: OBSERVED,
    endpoints,
    ...extra,
  }
}

/** The basic Simulated home: light (living room), plug (kitchen), AC (bedroom), door sensor and lock (no room). */
export function basicHome(): DeviceSummaryDto[] {
  return [
    device(
      'd-light',
      'Living room light',
      'light',
      [
        endpoint(
          'e-light',
          [
            capability(1, 'switch', [attr(1, 'on', bool(true))]),
            capability(2, 'level', [attr(2, 'level', num(80))], { attributes: { level: { min: 1 } } }),
          ],
          { effectiveRoomId: 10 },
        ),
      ],
      { roomId: 10 },
    ),
    device(
      'd-plug',
      'Coffee maker plug',
      'outlet',
      [
        endpoint(
          'e-plug',
          [
            capability(3, 'switch', [attr(3, 'on', bool(false))]),
            capability(4, 'power', [attr(4, 'power', num(0))]),
            capability(5, 'energy', [attr(5, 'energy', num(1250))]),
          ],
          { effectiveRoomId: 11 },
        ),
      ],
      { roomId: 11 },
    ),
    device(
      'd-ac',
      'Bedroom air conditioner',
      'air_conditioner',
      [
        endpoint(
          'e-ac',
          [
            capability(6, 'switch', [attr(6, 'on', bool(true))]),
            capability(
              7,
              'thermostat',
              [
                attr(7, 'mode', str('cool')),
                attr(8, 'targetTemperature', num(22)),
                attr(9, 'currentTemperature', num(26.4)),
                attr(10, 'fanMode', str('auto')),
              ],
              {
                attributes: {
                  mode: { values: ['off', 'cool', 'heat', 'auto', 'fan'] },
                  targetTemperature: {
                    min: 16,
                    max: 30,
                    step: 0.5,
                    dependsOn: 'mode',
                    cases: { heat: { max: 26 } },
                  },
                },
              },
            ),
            capability(8, 'temperature', [attr(11, 'temperature', num(26.4))]),
          ],
          { effectiveRoomId: 12 },
        ),
      ],
      { roomId: 12 },
    ),
    device('d-contact', 'Front door sensor', 'door', [
      endpoint('e-contact', [
        capability(9, 'contact', [attr(12, 'open', bool(false))]),
        capability(10, 'battery', [attr(13, 'battery', num(88))]),
      ]),
    ]),
    device('d-lock', 'Front door lock', 'lock', [
      endpoint('e-lock', [capability(11, 'lock', [attr(14, 'lockState', str('locked'))])]),
    ]),
  ]
}

export const ROOMS = [
  { id: 10, name: 'Living room', floorId: 1, floorName: 'Ground floor' },
  { id: 11, name: 'Kitchen', floorId: 1, floorName: 'Ground floor' },
  { id: 12, name: 'Bedroom', floorId: 1, floorName: 'Ground floor' },
]

export const INTEGRATIONS = [
  {
    id: 'int-1',
    connectorKey: 'simulated',
    name: 'Simulated home',
    status: 'Ok',
    statusMessage: null,
    enabled: true,
    deviceCount: 5,
    createdAtUtc: OBSERVED,
    updatedAtUtc: OBSERVED,
  },
]
