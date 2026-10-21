import type { DeviceSummaryDto, EndpointDto } from '@controlhome/api-client'
import { useI18n } from 'vue-i18n'
import { summarizeEndpoint, type EndpointSummary, type StateReader } from '../components/devices/deviceState'
import { useDevicesStore } from '../stores/devices'
import { useRealtimeStore } from '../stores/realtime'

/**
 * The live state of endpoints in one word and one color (`summarizeEndpoint` over the realtime values), for anything
 * that shows many endpoints at once: the floor plan's markers, the dashboard. Reactive when read inside a computed.
 */
export function useEndpointSummary() {
  const { t, te, locale } = useI18n()
  const realtime = useRealtimeStore()
  const devices = useDevicesStore()

  /** Reads the current (or optimistic) value of an endpoint's attribute by capability and key. */
  function readerOf(endpoint: Pick<EndpointDto, 'capabilities'>): StateReader {
    const ids = new Map<string, number>()
    for (const capability of endpoint.capabilities)
      for (const attribute of capability.attributes)
        ids.set(`${capability.capabilityId}.${attribute.key}`, attribute.attributeId)
    return (capabilityId, key) => realtime.valueOf(ids.get(`${capabilityId}.${key}`))
  }

  function summaryOf(
    device: Pick<DeviceSummaryDto, 'id' | 'availability' | 'deviceClass'>,
    endpoint: EndpointDto,
  ): EndpointSummary {
    return summarizeEndpoint(endpoint, readerOf(endpoint), {
      availability: devices.availabilityOf(device),
      deviceClass: device.deviceClass,
      locale: locale.value,
      t,
      te,
    })
  }

  return { readerOf, summaryOf }
}
