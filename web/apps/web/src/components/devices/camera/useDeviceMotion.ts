import type { DeviceSummaryDto } from '@controlhome/api-client'
import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import { asBool } from '../../../lib/stateValue'
import { useRealtimeStore } from '../../../stores/realtime'

/** Whether any endpoint of the device reports motion right now (a camera's own motion detection, tripwire, …). */
export function useDeviceMotion(device: MaybeRefOrGetter<Pick<DeviceSummaryDto, 'endpoints'>>) {
  const realtime = useRealtimeStore()
  const attributeIds = computed(() =>
    toValue(device).endpoints.flatMap((endpoint) =>
      endpoint.capabilities
        .filter((capability) => capability.capabilityId === 'motion' && !capability.retired)
        .flatMap((capability) => capability.attributes.filter((attribute) => attribute.key === 'motion'))
        .map((attribute) => attribute.attributeId),
    ),
  )
  return computed(() => attributeIds.value.some((id) => asBool(realtime.valueOf(id)) === true))
}
