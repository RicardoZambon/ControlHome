import { registerBuiltinControls } from './builtins'

// The registry with the built-in controls registered: import controls from here (registry.ts alone has none).
registerBuiltinControls()

export {
  primaryControl,
  registerCapabilityControl,
  registeredCapabilities,
  resolveControl,
  type CapabilityControlOptions,
  type ResolvedControl,
  type TileSlot,
} from './registry'
export type { CapabilityControlProps, ControlVariant } from './types'
export { default as CapabilityControl } from './CapabilityControl.vue'
