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

export const bool = (value: boolean): StateValue => ({ kind: 'bool', b: value })
export const num = (value: number): StateValue => ({ kind: 'number', n: value })
export const str = (value: string): StateValue => ({ kind: 'string', s: value })

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
    possibleDuplicateIds: [],
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

/** A Dahua-like camera: the main endpoint has the camera (with a stream) and its own motion detection. */
export function cameraDevice(options: { motion?: boolean; stream?: boolean } = {}): DeviceSummaryDto {
  return device('d-cam', 'Garden camera', 'camera', [
    endpoint('e-cam', [
      capability(40, 'camera', [attr(40, 'streamAvailable', bool(options.stream ?? true))]),
      capability(41, 'motion', [attr(41, 'motion', bool(options.motion ?? false))]),
    ]),
  ])
}

/** A Dahua-like access terminal: the door release, the door contact and the vendor's access events. */
export function accessTerminal(): DeviceSummaryDto {
  return device('d-vto', 'Gate terminal', 'intercom', [
    endpoint('e-vto', [
      capability(50, 'doorOpener', []),
      capability(51, 'contact', [attr(51, 'open', bool(false))]),
      capability(52, 'generic', []),
    ]),
  ])
}

/** A `generic` capability (a vendor's own attributes) with its definition. */
function generic(
  id: number,
  attributes: {
    key: string
    schema: ValueSchema
    value: StateValue
    diagnostic?: boolean
    writable?: boolean
  }[],
  commands: CommandDefinitionDto[] = [],
): EndpointCapabilityDto {
  return {
    ...capability(
      id,
      'generic',
      attributes.map((entry, index) => attr(id * 10 + index, entry.key, entry.value)),
    ),
    genericDefinition: {
      id: 'generic',
      version: 1,
      attributes: attributes.map((entry) =>
        attribute(entry.key, entry.schema, {
          category: entry.diagnostic ? 'diagnostic' : 'primary',
          writable: entry.writable ?? false,
        }),
      ),
      commands,
    },
  }
}

/**
 * A SmartThings-like fridge: the main endpoint (door, settings and the vendor's metadata), the freezer and the ice maker
 * (unnamed: the connector used to store the component id as the name), and `hca_main`, an endpoint the integration no
 * longer reports.
 */
export function fridge(): DeviceSummaryDto {
  const text: ValueSchema = { type: 'string', maxLength: null }
  return device(
    'd-fridge',
    'Kitchen fridge',
    'refrigerator',
    [
      endpoint(
        'e-fridge',
        [
          capability(60, 'contact', [attr(60, 'open', bool(false))]),
          generic(61, [
            { key: 'waterFilterStatus', schema: text, value: str('replace') },
            { key: 'refrigerationRapidCooling', schema: text, value: str('off') },
            // An empty text (a TV's channel name while it shows an input) says nothing.
            { key: 'displayMessage', schema: text, value: str('') },
            { key: 'energyType', schema: text, value: str('2.0'), diagnostic: true },
            {
              key: 'waterFilterResetType',
              schema: { type: 'json' },
              value: { kind: 'json', j: ['replaceable'] } as StateValue,
              diagnostic: true,
            },
          ]),
        ],
        { effectiveRoomId: 11 },
      ),
      endpoint(
        'e-freezer',
        [
          capability(62, 'temperature', [attr(62, 'temperature', num(-17.78))]),
          generic(
            63,
            [
              {
                key: 'coolingSetpoint',
                schema: { type: 'number', min: -22.22, max: -15, step: null, unit: '°C', integer: false },
                value: num(-17.78),
                writable: true,
              },
            ],
            [
              command('setCoolingSetpoint', {
                arguments: [
                  {
                    key: 'coolingSetpoint',
                    schema: { type: 'number', min: -22.22, max: -15, step: null, unit: '°C', integer: false },
                    required: true,
                    attribute: 'coolingSetpoint',
                  },
                ],
              }),
            ],
          ),
        ],
        { key: 'freezer', name: 'freezer', effectiveRoomId: 11, sortOrder: 1 },
      ),
      endpoint(
        'e-icemaker',
        [
          capability(64, 'switch', [attr(64, 'on', bool(true))]),
          generic(65, [
            { key: 'fridgeIcemakerInfoName', schema: text, value: str('ICE_MAKER'), diagnostic: true },
          ]),
        ],
        { key: 'icemaker-02', effectiveRoomId: 11, sortOrder: 2 },
      ),
      endpoint(
        'e-hca',
        [{ ...generic(66, [{ key: 'washerMode', schema: text, value: str('normal') }]), retired: true }],
        { key: 'hca_main', name: 'hca.main', effectiveRoomId: 11, sortOrder: 3 },
      ),
    ],
    { roomId: 11 },
  )
}

/** A 3-gang wall switch: three channels, each its own tile (the first one named). */
export function wallSwitch(): DeviceSummaryDto {
  const channel = (index: number, extra: Partial<EndpointDto> = {}) =>
    endpoint(`e-gang-${index}`, [capability(70 + index, 'switch', [attr(70 + index, 'on', bool(false))])], {
      key: `l${index}`,
      sortOrder: index,
      ...extra,
    })
  return device('d-gang', 'Wall switch', 'switch', [channel(1, { name: 'Ceiling' }), channel(2), channel(3)])
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
