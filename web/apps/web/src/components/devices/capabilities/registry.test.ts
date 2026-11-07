import { defineComponent } from 'vue'
import { afterEach, describe, expect, it } from 'vitest'
import { accessTerminal, cameraDevice } from '../../../test/devices'
import { layoutOf } from '../endpointLayout'
import CameraControl from './CameraControl.vue'
import DoorOpenerControl from './DoorOpenerControl.vue'
import GenericCapabilityControl from './GenericCapabilityControl.vue'
import { registerBuiltinControls } from './builtins'
import {
  primaryControl,
  registerCapabilityControl,
  registeredCapabilities,
  resolveControl,
  unregisterCapabilityControl,
} from './registry'

const Custom = defineComponent({ name: 'CustomControl', render: () => null })

afterEach(() => unregisterCapabilityControl('custom'))

describe('capability-control registry', () => {
  it('falls back to the generic control for a capability without one', () => {
    const resolved = resolveControl('vendorThing')
    expect(resolved.component).toBe(GenericCapabilityControl)
    expect(resolved.fallback).toBe(true)
    expect(resolved.options).toEqual({ kind: 'control', order: 900, wide: true, tileSlot: undefined })
  })

  it('resolves a registered control with its options', () => {
    registerCapabilityControl('custom', Custom, { tileSlot: 'secondary', kind: 'reading', order: 7 })
    const resolved = resolveControl('custom')
    expect(resolved.component).toBe(Custom)
    expect(resolved.fallback).toBe(false)
    expect(resolved.options).toEqual({ kind: 'reading', order: 7, wide: false, tileSlot: 'secondary' })
  })

  it('replaces a registration and can remove it again', () => {
    registerCapabilityControl('custom', GenericCapabilityControl)
    registerCapabilityControl('custom', Custom)
    expect(resolveControl('custom').component).toBe(Custom)
    unregisterCapabilityControl('custom')
    expect(resolveControl('custom').fallback).toBe(true)
  })

  it('registers a control for every capability of the catalogue but the alarm panel and generic', () => {
    registerBuiltinControls()
    expect(registeredCapabilities().sort()).toEqual(
      [
        'airQuality',
        'applianceCycle',
        'battery',
        'button',
        'camera',
        'color',
        'contact',
        'cover',
        'doorOpener',
        'energy',
        'humidity',
        'level',
        'lock',
        'mediaPlayer',
        'motion',
        'occupancy',
        'power',
        'presence',
        'smoke',
        'switch',
        'temperature',
        'thermostat',
        'waterLeak',
      ].sort(),
    )
    expect(resolveControl('alarmPanel').fallback).toBe(true)
    expect(resolveControl('generic').fallback).toBe(true)
  })

  it("picks the tile's quick action: the primary control with the lowest order", () => {
    registerBuiltinControls()
    // An AC: power is the quick action, not the thermostat or the temperature.
    expect(primaryControl(['thermostat', 'temperature', 'switch'])?.capabilityId).toBe('switch')
    // A lock wins over a switch on the same endpoint.
    expect(primaryControl(['switch', 'lock'])?.capabilityId).toBe('lock')
    // Sensors only: no quick action.
    expect(primaryControl(['contact', 'battery'])).toBeUndefined()
    expect(primaryControl([])).toBeUndefined()
  })

  it('reaches the door opener (PIN step-up) on a Dahua-like access terminal', () => {
    registerBuiltinControls()
    const terminal = accessTerminal().endpoints[0]!
    const ids = terminal.capabilities.map((capability) => capability.capabilityId)
    expect(primaryControl(ids)?.capabilityId).toBe('doorOpener')
    expect(primaryControl(ids)?.component).toBe(DoorOpenerControl)
    expect(layoutOf(terminal).controls.map((entry) => entry.capability.capabilityId)).toContain('doorOpener')
  })

  it('lays out a camera: the camera control full width, its motion as a reading', () => {
    registerBuiltinControls()
    const camera = cameraDevice().endpoints[0]!
    expect(resolveControl('camera').component).toBe(CameraControl)
    const layout = layoutOf(camera)
    expect(layout.controls).toEqual([expect.objectContaining({ full: true })])
    expect(layout.controls[0]!.capability.capabilityId).toBe('camera')
    expect(layout.readings.map((capability) => capability.capabilityId)).toEqual(['motion'])
  })
})
