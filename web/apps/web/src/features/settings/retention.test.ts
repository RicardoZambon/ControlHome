import { describe, expect, it } from 'vitest'
import { retentionProblems } from './retention'

describe('retentionProblems', () => {
  it('accepts whole days within the server’s limits', () => {
    expect(
      retentionProblems({
        stateLogDays: '10',
        shortTermStatsDays: '10',
        deviceEventDays: '30',
        auditLogDays: '365',
      }),
    ).toEqual({})
  })

  it('flags empty, fractional and out-of-range values', () => {
    expect(
      retentionProblems({
        stateLogDays: '',
        shortTermStatsDays: '1.5',
        deviceEventDays: '366',
        auditLogDays: '29',
      }),
    ).toEqual({ stateLogDays: true, shortTermStatsDays: true, deviceEventDays: true, auditLogDays: true })
  })
})
