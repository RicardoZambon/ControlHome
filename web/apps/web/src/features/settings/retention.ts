import type { RetentionSettingsDto } from '@controlhome/api-client'

/** The retention fields with the server's limits (`UpdateRetentionSettingsRequestValidator`). */
export type RetentionField = keyof RetentionSettingsDto

export const RETENTION_FIELDS: readonly { key: RetentionField; min: number; max: number }[] = [
  { key: 'stateLogDays', min: 1, max: 365 },
  { key: 'shortTermStatsDays', min: 1, max: 365 },
  { key: 'deviceEventDays', min: 1, max: 365 },
  { key: 'auditLogDays', min: 30, max: 3650 },
]

/** Why a value can't be saved: not a whole number of days within the limits, or averages kept shorter than changes. */
export type RetentionProblem = 'range' | 'shortBelowRaw'

/**
 * The fields whose typed value can't be saved: not a whole number of days within its limits, or 5-minute averages kept
 * for less time than every change (the averages are what charts fall back to once the changes are gone).
 */
export function retentionProblems(
  values: Record<RetentionField, string>,
): Partial<Record<RetentionField, RetentionProblem>> {
  const problems: Partial<Record<RetentionField, RetentionProblem>> = {}
  for (const field of RETENTION_FIELDS) {
    const text = values[field.key].trim()
    const value = Number(text)
    if (!text || !Number.isInteger(value) || value < field.min || value > field.max)
      problems[field.key] = 'range'
  }
  if (!problems.stateLogDays && !problems.shortTermStatsDays) {
    if (Number(values.shortTermStatsDays) < Number(values.stateLogDays))
      problems.shortTermStatsDays = 'shortBelowRaw'
  }
  return problems
}

/** How much of the bar "how a reading ages" each tier takes (percent), on a log scale of days; forever gets the rest. */
export function tierWidths(stateLogDays: number, shortTermStatsDays: number): [number, number, number] {
  const days = (value: number) => (Number.isFinite(value) ? Math.max(0, value) : 0)
  const scale = (value: number) => (Math.log1p(days(value)) / Math.log1p(365)) * 80
  const raw = scale(stateLogDays)
  const short = Math.max(0, scale(Math.max(days(shortTermStatsDays), days(stateLogDays))) - raw)
  return [raw, short, 100 - raw - short]
}
