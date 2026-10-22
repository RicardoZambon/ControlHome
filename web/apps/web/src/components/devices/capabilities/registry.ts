import { markRaw, type Component } from 'vue'
import GenericCapabilityControl from './GenericCapabilityControl.vue'

/**
 * The one capability-control registry (docs/architecture.md §11): capability id → the component that controls it, used
 * by device tiles, the device page and, later, the floor-plan popover and the scene and automation editors. A
 * capability without a registered control falls back to {@link GenericCapabilityControl}, which renders its writable
 * attributes with `<SchemaField>` and its commands as buttons. Built-in controls are registered in `builtins.ts`.
 */

/** On tiles: `primary` is the quick action (the first by order); `secondary` capabilities are summarized in words. */
export type TileSlot = 'primary' | 'secondary'

export interface CapabilityControlOptions {
  tileSlot?: TileSlot
  /** `reading`: a sensor value, shown with the other readings on the device page. Default `control`. */
  kind?: 'control' | 'reading'
  /** Position on the device page and among tile quick actions (ascending). Default 500. */
  order?: number
  /** Takes the full width of the device page's control grid. */
  wide?: boolean
}

export interface ResolvedControl {
  capabilityId: string
  component: Component
  options: Required<Omit<CapabilityControlOptions, 'tileSlot'>> & { tileSlot?: TileSlot }
  /** No control is registered: the generic one is used. */
  fallback: boolean
}

interface Registration {
  component: Component
  options: CapabilityControlOptions
}

const registrations = new Map<string, Registration>()

const GENERIC_OPTIONS: CapabilityControlOptions = { kind: 'control', order: 900, wide: true }

/** Registers (or replaces) the control of a capability. */
export function registerCapabilityControl(
  capabilityId: string,
  component: Component,
  options: CapabilityControlOptions = {},
): void {
  registrations.set(capabilityId, { component: markRaw(component), options })
}

/** Removes a registration (tests). */
export function unregisterCapabilityControl(capabilityId: string): void {
  registrations.delete(capabilityId)
}

/** The control of a capability, or the generic one. */
export function resolveControl(capabilityId: string): ResolvedControl {
  const registration = registrations.get(capabilityId)
  const options = registration?.options ?? GENERIC_OPTIONS
  return {
    capabilityId,
    component: registration?.component ?? GenericCapabilityControl,
    options: {
      kind: options.kind ?? 'control',
      order: options.order ?? 500,
      wide: options.wide ?? false,
      tileSlot: options.tileSlot,
    },
    fallback: !registration,
  }
}

/** The capabilities that have a registered control. */
export function registeredCapabilities(): string[] {
  return [...registrations.keys()]
}

/** The tile quick action of an endpoint: the primary control with the lowest order, if any. */
export function primaryControl(capabilityIds: readonly string[]): ResolvedControl | undefined {
  return capabilityIds
    .map(resolveControl)
    .filter((control) => control.options.tileSlot === 'primary')
    .sort((a, b) => a.options.order - b.options.order)[0]
}
