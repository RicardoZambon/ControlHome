import { describe, expect, it } from 'vitest'
import { retentionProblems, tierWidths } from './retention'

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
    ).toEqual({
      stateLogDays: 'range',
      shortTermStatsDays: 'range',
      deviceEventDays: 'range',
      auditLogDays: 'range',
    })
  })

  it('keeps 5-minute averages at least as long as every change (they take over from it)', () => {
    expect(
      retentionProblems({
        stateLogDays: '30',
        shortTermStatsDays: '10',
        deviceEventDays: '30',
        auditLogDays: '365',
      }),
    ).toEqual({ shortTermStatsDays: 'shortBelowRaw' })
  })
})

describe('tierWidths', () => {
  it('draws how long each tier lasts on a log scale, forever taking what is left', () => {
    const [raw, short, hourly] = tierWidths(10, 10)
    expect(raw).toBeGreaterThan(0)
    expect(short).toBe(0)
    expect(raw! + short! + hourly!).toBeCloseTo(100)

    const [raw2, short2, hourly2] = tierWidths(2, 30)
    expect(raw2).toBeLessThan(short2!)
    expect(hourly2).toBeGreaterThanOrEqual(20)
  })

  it('never draws a negative tier for values not typed yet', () => {
    expect(tierWidths(Number.NaN, Number.NaN).every((width) => width >= 0)).toBe(true)
  })
})
