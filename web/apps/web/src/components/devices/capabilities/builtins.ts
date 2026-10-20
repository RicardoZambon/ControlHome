import AirQualityControl from './AirQualityControl.vue'
import ApplianceCycleControl from './ApplianceCycleControl.vue'
import BinarySensorControl from './BinarySensorControl.vue'
import ButtonControl from './ButtonControl.vue'
import CameraControl from './CameraControl.vue'
import ColorControl from './ColorControl.vue'
import CoverControl from './CoverControl.vue'
import DoorOpenerControl from './DoorOpenerControl.vue'
import LevelControl from './LevelControl.vue'
import LockControl from './LockControl.vue'
import MeasurementControl from './MeasurementControl.vue'
import MediaPlayerControl from './MediaPlayerControl.vue'
import SwitchControl from './SwitchControl.vue'
import ThermostatControl from './ThermostatControl.vue'
import { registerCapabilityControl } from './registry'

/**
 * The built-in capability controls. Order: the main control of each kind of device first (its tile quick action is
 * the `primary` control with the lowest order), then adjustments, then readings. `alarmPanel` and `generic` use the
 * generic control.
 */
let registered = false

export function registerBuiltinControls(): void {
  if (registered) return
  registered = true

  // Actuators: the tile quick action.
  registerCapabilityControl('lock', LockControl, { tileSlot: 'primary', order: 10 })
  registerCapabilityControl('doorOpener', DoorOpenerControl, { tileSlot: 'primary', order: 12 })
  registerCapabilityControl('switch', SwitchControl, { tileSlot: 'primary', order: 14 })
  registerCapabilityControl('cover', CoverControl, { tileSlot: 'primary', order: 16, wide: true })
  registerCapabilityControl('mediaPlayer', MediaPlayerControl, { tileSlot: 'primary', order: 18, wide: true })
  registerCapabilityControl('applianceCycle', ApplianceCycleControl, {
    tileSlot: 'primary',
    order: 20,
    wide: true,
  })

  // Adjustments.
  registerCapabilityControl('level', LevelControl, { tileSlot: 'secondary', order: 30 })
  registerCapabilityControl('color', ColorControl, { tileSlot: 'secondary', order: 32 })
  registerCapabilityControl('thermostat', ThermostatControl, { tileSlot: 'secondary', order: 34, wide: true })
  registerCapabilityControl('camera', CameraControl, { tileSlot: 'secondary', order: 40, wide: true })

  // Readings.
  const reading = (order: number) => ({ tileSlot: 'secondary' as const, kind: 'reading' as const, order })
  registerCapabilityControl('smoke', BinarySensorControl, reading(100))
  registerCapabilityControl('waterLeak', BinarySensorControl, reading(101))
  registerCapabilityControl('temperature', MeasurementControl, reading(110))
  registerCapabilityControl('humidity', MeasurementControl, reading(111))
  registerCapabilityControl('airQuality', AirQualityControl, reading(112))
  registerCapabilityControl('power', MeasurementControl, reading(120))
  registerCapabilityControl('energy', MeasurementControl, reading(121))
  registerCapabilityControl('contact', BinarySensorControl, reading(130))
  registerCapabilityControl('motion', BinarySensorControl, reading(131))
  registerCapabilityControl('occupancy', BinarySensorControl, reading(132))
  registerCapabilityControl('presence', BinarySensorControl, reading(133))
  registerCapabilityControl('button', ButtonControl, reading(140))
  registerCapabilityControl('battery', MeasurementControl, reading(150))
}
