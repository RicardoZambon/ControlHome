import { DoorClosed, DoorOpen, Lightbulb, Lock, LockOpen } from '@lucide/vue'
import { describe, expect, it } from 'vitest'
import { deviceIcon } from './deviceIcons'

describe('deviceIcon', () => {
  it('by its own icon, else its class, else its capabilities', () => {
    expect(deviceIcon({ icon: 'lock', deviceClass: 'light' })).toBe(Lock)
    expect(deviceIcon({ deviceClass: 'light', capabilities: ['lock'] })).toBe(Lightbulb)
    expect(deviceIcon({ capabilities: ['battery', 'contact'] })).toBe(DoorClosed)
  })

  it('shows a lock or a door open while it stands open', () => {
    expect(deviceIcon({ deviceClass: 'lock', open: true })).toBe(LockOpen)
    expect(deviceIcon({ capabilities: ['contact'], open: true })).toBe(DoorOpen)
    expect(deviceIcon({ deviceClass: 'light', open: true })).toBe(Lightbulb)
  })
})
