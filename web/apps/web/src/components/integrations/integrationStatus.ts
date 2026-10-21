import { Blocks, FlaskConical, Plug, RadioTower, Wifi, type LucideIcon } from '@lucide/vue'

/** How an integration status looks: its dot color, whether it pulses (working on it) and whether it needs someone. */
export interface StatusStyle {
  tone: string
  pulse?: boolean
  /** Something to do (sign in again, a plan, an error): highlighted on the dashboard. */
  attention?: boolean
}

/** `IntegrationDto.status` values (docs/architecture.md §4 `SetStatus`). */
export const STATUS_STYLES: Record<string, StatusStyle> = {
  Ok: { tone: 'bg-state-on' },
  Connecting: { tone: 'bg-state-info', pulse: true },
  Degraded: { tone: 'bg-state-warn', attention: true },
  NeedsReauth: { tone: 'bg-state-warn', attention: true },
  PlanRequired: { tone: 'bg-state-warn', attention: true },
  QuotaExceeded: { tone: 'bg-state-warn', attention: true },
  Error: { tone: 'bg-state-danger', attention: true },
  Stopped: { tone: 'bg-state-off' },
}

export function statusStyle(status: string): StatusStyle {
  return STATUS_STYLES[status] ?? { tone: 'bg-state-off' }
}

/** Connector icon keys (`ConnectorDto.icon`) → icons; unknown keys get the generic block. */
const CONNECTOR_ICONS: Record<string, LucideIcon> = {
  'flask-conical': FlaskConical,
  'radio-tower': RadioTower,
  plug: Plug,
  wifi: Wifi,
}

export function connectorIcon(key: string | null | undefined): LucideIcon {
  return (key && CONNECTOR_ICONS[key]) || Blocks
}
