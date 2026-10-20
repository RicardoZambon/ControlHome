import type { DeviceSummaryDto, EndpointCapabilityDto, EndpointDto } from '@controlhome/api-client'

/** Where a control is drawn: a tile's quick action (`tile`) or the device page (`detail`). */
export type ControlVariant = 'tile' | 'detail'

/** The props every capability control receives. */
export interface CapabilityControlProps {
  device: Pick<DeviceSummaryDto, 'id' | 'name' | 'deviceClass' | 'availability' | 'isOrphaned' | 'endpoints'>
  endpoint: EndpointDto
  capability: EndpointCapabilityDto
  variant?: ControlVariant
}
