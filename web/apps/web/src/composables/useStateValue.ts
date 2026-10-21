import type { EndpointCapabilityDto, StateValue } from '@controlhome/api-client'
import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import { asBool, asNumber, asString, fromStateValue } from '../lib/stateValue'
import { useRealtimeStore, type LiveValue } from '../stores/realtime'

/** The live value of one attribute (the optimistic one while a command for it is pending). */
export function useStateValue(attributeId: MaybeRefOrGetter<number | undefined>) {
  const realtime = useRealtimeStore()
  return computed<StateValue | undefined>(() => realtime.valueOf(toValue(attributeId)))
}

/** Reads the attributes of one endpoint capability by key, live (call the readers in templates or computeds). */
export function useCapabilityState(capability: MaybeRefOrGetter<EndpointCapabilityDto | undefined>) {
  const realtime = useRealtimeStore()
  const ids = computed(
    () =>
      new Map(
        toValue(capability)?.attributes.map((attribute) => [attribute.key, attribute.attributeId]) ?? [],
      ),
  )

  const attributeId = (key: string): number | undefined => ids.value.get(key)
  const value = (key: string): StateValue | undefined => realtime.valueOf(attributeId(key))
  const live = (key: string): LiveValue | undefined => realtime.liveOf(attributeId(key))

  return {
    attributeId,
    /** Whether the endpoint reported (or declared) this attribute. */
    has: (key: string) => ids.value.has(key),
    value,
    live,
    plain: (key: string) => fromStateValue(value(key)),
    bool: (key: string) => asBool(value(key)),
    number: (key: string) => asNumber(value(key)),
    string: (key: string) => asString(value(key)),
  }
}

export type CapabilityState = ReturnType<typeof useCapabilityState>
