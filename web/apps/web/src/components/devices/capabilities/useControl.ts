import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useCommand } from '../../../composables/useCommand'
import { useCapabilityState } from '../../../composables/useStateValue'
import { endpointName, useDevicesStore } from '../../../stores/devices'
import { constraintFor } from '../../schema/schema'
import { attributeLabel, commandLabel, formatMeasurement, valueLabel } from '../format'
import type { CapabilityControlProps } from './types'

/**
 * What every capability control needs: the capability's live values, its definition and constraints, whether the
 * person may control it now (Member+, device reachable and still reported), and `send` with the device class and a
 * readable action name for the PIN dialog.
 */
export function useControl(props: CapabilityControlProps) {
  const { t, te, locale } = useI18n()
  const devices = useDevicesStore()
  const state = useCapabilityState(() => props.capability)
  const commands = useCommand(() => props.endpoint.id)

  const definition = computed(() => devices.definitionOf(props.capability))
  const availability = computed(() => devices.availabilityOf(props.device))
  const offline = computed(() => availability.value === 'Offline')
  const name = computed(() => endpointName(props.device, props.endpoint))
  /** Commands can be sent: a Member or Admin, the device is reachable and still reported by its integration. */
  const enabled = computed(() => commands.canControl.value && !offline.value && !props.device.isOrphaned)

  const hasCommand = (command: string) =>
    definition.value?.commands.some((entry) => entry.name === command) ?? false

  return {
    t,
    te,
    locale,
    state,
    definition,
    availability,
    offline,
    name,
    enabled,
    canControl: commands.canControl,
    hasCommand,
    /** The endpoint's constraint for an attribute, with the case for the current mode applied. */
    constraint: (key: string) =>
      constraintFor(props.capability.constraints, key, (other) => state.plain(other)),
    /** A command of this capability is pending (optionally a given one). */
    pending: (command?: string) => commands.isPending(props.capability.capabilityId, command),
    send: (command: string, args?: Record<string, unknown>) =>
      commands.send(props.capability, command, args, {
        deviceClass: props.device.deviceClass,
        actionLabel: `${commandLabel(command, t, te)} · ${name.value}`,
      }),
    measure: (value: number, unit: string | null | undefined) => formatMeasurement(value, unit, locale.value),
    valueLabel: (attributeKey: string, value: string) => valueLabel(attributeKey, value, t, te),
    commandLabel: (command: string) => commandLabel(command, t, te),
    attributeLabel: (key: string) => attributeLabel(key, t, te, props.capability.capabilityId),
  }
}
