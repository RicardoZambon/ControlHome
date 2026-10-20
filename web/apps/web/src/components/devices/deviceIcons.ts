import {
  Activity,
  AirVent,
  AlarmSmoke,
  AppWindow,
  BellRing,
  Blinds,
  CircleDot,
  CircuitBoard,
  Coffee,
  Cctv,
  DoorClosed,
  DoorOpen,
  Droplets,
  Fan,
  Fence,
  HardDrive,
  Heater,
  Lamp,
  LampCeiling,
  LampDesk,
  LampFloor,
  Lightbulb,
  Lock,
  Microwave,
  MonitorSpeaker,
  Phone,
  Plug,
  Radar,
  Refrigerator,
  ShieldAlert,
  Speaker,
  Thermometer,
  ToggleRight,
  Tv,
  Warehouse,
  WashingMachine,
  Wind,
  Zap,
} from '@lucide/vue'
import type { Component } from 'vue'

/**
 * Device icons: the key stored on a device or endpoint (`Device.Icon`, a lucide name in kebab case) → its component.
 * Admins pick from this list; without an icon, the device class decides, then the capabilities.
 */
export const DEVICE_ICONS: readonly { key: string; icon: Component }[] = [
  { key: 'lightbulb', icon: Lightbulb },
  { key: 'lamp', icon: Lamp },
  { key: 'lamp-ceiling', icon: LampCeiling },
  { key: 'lamp-desk', icon: LampDesk },
  { key: 'lamp-floor', icon: LampFloor },
  { key: 'plug', icon: Plug },
  { key: 'toggle-right', icon: ToggleRight },
  { key: 'air-vent', icon: AirVent },
  { key: 'fan', icon: Fan },
  { key: 'heater', icon: Heater },
  { key: 'thermometer', icon: Thermometer },
  { key: 'droplets', icon: Droplets },
  { key: 'wind', icon: Wind },
  { key: 'tv', icon: Tv },
  { key: 'speaker', icon: Speaker },
  { key: 'monitor-speaker', icon: MonitorSpeaker },
  { key: 'lock', icon: Lock },
  { key: 'door-closed', icon: DoorClosed },
  { key: 'door-open', icon: DoorOpen },
  { key: 'app-window', icon: AppWindow },
  { key: 'blinds', icon: Blinds },
  { key: 'fence', icon: Fence },
  { key: 'warehouse', icon: Warehouse },
  { key: 'washing-machine', icon: WashingMachine },
  { key: 'refrigerator', icon: Refrigerator },
  { key: 'microwave', icon: Microwave },
  { key: 'coffee', icon: Coffee },
  { key: 'cctv', icon: Cctv },
  { key: 'bell-ring', icon: BellRing },
  { key: 'shield-alert', icon: ShieldAlert },
  { key: 'alarm-smoke', icon: AlarmSmoke },
  { key: 'activity', icon: Activity },
  { key: 'radar', icon: Radar },
  { key: 'zap', icon: Zap },
  { key: 'circuit-board', icon: CircuitBoard },
]

const byKey = new Map(DEVICE_ICONS.map((entry) => [entry.key, entry.icon]))

const byClass: Record<string, Component> = {
  light: Lightbulb,
  outlet: Plug,
  switch: ToggleRight,
  air_conditioner: AirVent,
  thermostat: Thermometer,
  door: DoorClosed,
  window: AppWindow,
  lock: Lock,
  gate: Fence,
  garage: Warehouse,
  blind: Blinds,
  curtain: Blinds,
  sensor: Radar,
  tv: Tv,
  speaker: Speaker,
  washer: WashingMachine,
  dryer: WashingMachine,
  camera: Cctv,
  nvr: HardDrive,
  alarm_panel: ShieldAlert,
  doorbell: BellRing,
  intercom: Phone,
  button: CircleDot,
}

/** By the first capability that says what the device is. */
const byCapability: [string, Component][] = [
  ['lock', Lock],
  ['doorOpener', DoorOpen],
  ['cover', Blinds],
  ['thermostat', AirVent],
  ['mediaPlayer', Tv],
  ['camera', Cctv],
  ['applianceCycle', WashingMachine],
  ['alarmPanel', ShieldAlert],
  ['smoke', AlarmSmoke],
  ['waterLeak', Droplets],
  ['color', Lightbulb],
  ['level', Lightbulb],
  ['switch', Plug],
  ['contact', DoorClosed],
  ['motion', Activity],
  ['occupancy', Activity],
  ['presence', Radar],
  ['airQuality', Wind],
  ['temperature', Thermometer],
  ['humidity', Droplets],
  ['power', Zap],
  ['energy', Zap],
  ['button', CircleDot],
]

/** The icon of a device: its own, else its class's, else its capabilities'. */
export function deviceIcon(options: {
  icon?: string | null
  deviceClass?: string | null
  capabilities?: readonly string[]
}): Component {
  if (options.icon) {
    const own = byKey.get(options.icon)
    if (own) return own
  }
  if (options.deviceClass) {
    const fromClass = byClass[options.deviceClass]
    if (fromClass) return fromClass
  }
  const capabilities = new Set(options.capabilities ?? [])
  for (const [capability, icon] of byCapability) if (capabilities.has(capability)) return icon
  return CircuitBoard
}
