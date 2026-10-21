import type { RetentionSettingsDto } from '@controlhome/api-client'

/** The retention fields with the server's limits (`UpdateRetentionSettingsRequestValidator`). */
export type RetentionField = keyof RetentionSettingsDto

export const RETENTION_FIELDS: readonly { key: RetentionField; min: number; max: number }[] = [
  { key: 'stateLogDays', min: 1, max: 365 },
  { key: 'shortTermStatsDays', min: 1, max: 365 },
  { key: 'deviceEventDays', min: 1, max: 365 },
  { key: 'auditLogDays', min: 30, max: 3650 },
]

/** The fields whose typed value is not a whole number of days within its limits. */
export function retentionProblems(
  values: Record<RetentionField, string>,
): Partial<Record<RetentionField, true>> {
  const problems: Partial<Record<RetentionField, true>> = {}
  for (const field of RETENTION_FIELDS) {
    const text = values[field.key].trim()
    const value = Number(text)
    if (!text || !Number.isInteger(value) || value < field.min || value > field.max)
      problems[field.key] = true
  }
  return problems
}
