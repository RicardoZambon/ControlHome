import { describe, expect, it } from 'vitest'
import type { EndpointSummary, Tone } from '../devices/deviceState'
import { MARKER_TONES, combineSummaries } from './markerTone'

const summary = (tone: Tone, active = false, label: string = tone): EndpointSummary => ({
  tone,
  label,
  details: [],
  active,
})

describe('combineSummaries', () => {
  it('shows the only endpoint as it is', () => {
    expect(combineSummaries([summary('off')]).tone).toBe('off')
  })

  it('puts alarms and warnings first', () => {
    expect(combineSummaries([summary('on', true), summary('warn'), summary('danger')]).tone).toBe('danger')
    expect(combineSummaries([summary('on', true), summary('warn')]).tone).toBe('warn')
  })

  it('is offline only when every endpoint is', () => {
    expect(combineSummaries([summary('offline'), summary('offline')]).tone).toBe('offline')
    expect(combineSummaries([summary('offline'), summary('off')]).tone).toBe('offline')
    expect(combineSummaries([summary('off'), summary('offline')]).tone).toBe('off')
  })

  it('prefers an endpoint that is on', () => {
    expect(combineSummaries([summary('off'), summary('on', true, 'l2 on')]).label).toBe('l2 on')
  })

  it('is neutral without endpoints', () => {
    expect(combineSummaries([]).tone).toBe('neutral')
  })

  it('has ring classes for every tone, dashed only offline', () => {
    for (const [tone, classes] of Object.entries(MARKER_TONES)) {
      expect(classes.ring, tone).toMatch(/^stroke-/)
      expect(classes.dashed, tone).toBe(tone === 'offline')
    }
  })
})
