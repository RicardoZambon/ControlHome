import { describe, expect, it } from 'vitest'
import {
  cmToScreen,
  fitView,
  gridSteps,
  panBy,
  pinchView,
  scaleBarLength,
  scaleLimits,
  screenToCm,
  viewBoxOf,
  zoomAt,
  type View,
} from './usePanZoom'

const size = { width: 800, height: 600 }

describe('fitView', () => {
  it('centres the content and fits it inside the padding', () => {
    const view = fitView({ x: 0, y: 0, width: 2000, height: 1500 }, size, 20)
    expect(view.cx).toBe(1000)
    expect(view.cy).toBe(750)
    // (800 − 40) / 2000 = 0.38 and (600 − 40) / 1500 ≈ 0.373: the height decides.
    expect(view.scale).toBeCloseTo(560 / 1500, 9)
  })

  it('survives a container without size', () => {
    const view = fitView({ x: 0, y: 0, width: 2000, height: 1500 }, { width: 0, height: 0 }, 20)
    expect(view.scale).toBeGreaterThan(0)
    expect(Number.isFinite(view.scale)).toBe(true)
  })
})

describe('viewBoxOf / screenToCm / cmToScreen', () => {
  const view: View = { cx: 1000, cy: 750, scale: 0.5 }

  it('the view box covers the container at the scale, centred on the view', () => {
    expect(viewBoxOf(view, size)).toEqual({ x: 200, y: 150, width: 1600, height: 1200 })
  })

  it('maps screen pixels to centimetres and back', () => {
    expect(screenToCm(view, size, { x: 400, y: 300 })).toEqual({ x: 1000, y: 750 })
    expect(screenToCm(view, size, { x: 0, y: 0 })).toEqual({ x: 200, y: 150 })
    expect(cmToScreen(view, size, { x: 1200, y: 750 })).toEqual({ x: 500, y: 300 })
  })
})

describe('zoomAt', () => {
  const limits = { min: 0.1, max: 10 }

  it('keeps the point under the cursor in place', () => {
    const view: View = { cx: 1000, cy: 750, scale: 0.5 }
    const anchor = { x: 100, y: 120 }
    const before = screenToCm(view, size, anchor)
    const zoomed = zoomAt(view, 2, anchor, size, limits)
    expect(zoomed.scale).toBe(1)
    const after = screenToCm(zoomed, size, anchor)
    expect(after.x).toBeCloseTo(before.x, 9)
    expect(after.y).toBeCloseTo(before.y, 9)
  })

  it('clamps the scale to the limits', () => {
    const view: View = { cx: 0, cy: 0, scale: 8 }
    expect(zoomAt(view, 4, { x: 400, y: 300 }, size, limits).scale).toBe(10)
    expect(zoomAt(view, 0.0001, { x: 400, y: 300 }, size, limits).scale).toBe(0.1)
  })
})

describe('panBy', () => {
  it('moves the content with the pointer', () => {
    const view: View = { cx: 1000, cy: 750, scale: 0.5 }
    // Dragging 100 px to the right shows what was 200 cm to the left.
    expect(panBy(view, 100, -50)).toEqual({ cx: 800, cy: 850, scale: 0.5 })
  })
})

describe('pinchView', () => {
  it('zooms by the change of finger distance around their midpoint and follows the midpoint', () => {
    const view: View = { cx: 1000, cy: 750, scale: 0.5 }
    const before = [
      { x: 300, y: 300 },
      { x: 500, y: 300 },
    ] as const
    const after = [
      { x: 250, y: 300 },
      { x: 550, y: 300 },
    ] as const
    const next = pinchView(view, before, after, size, { min: 0.1, max: 10 })
    expect(next.scale).toBeCloseTo(0.75, 9)
    // The cm point under the fingers' midpoint stays under it.
    const anchorBefore = screenToCm(view, size, { x: 400, y: 300 })
    const anchorAfter = screenToCm(next, size, { x: 400, y: 300 })
    expect(anchorAfter.x).toBeCloseTo(anchorBefore.x, 9)
    expect(anchorAfter.y).toBeCloseTo(anchorBefore.y, 9)
  })
})

describe('scaleLimits', () => {
  it('allows zooming out to a quarter of the fit and in to at least 8 px per cm', () => {
    expect(scaleLimits(0.4)).toEqual({ min: 0.1, max: 8 })
    expect(scaleLimits(4)).toEqual({ min: 1, max: 16 })
  })
})

describe('scaleBarLength', () => {
  it('picks the longest round length that fits in 120 px', () => {
    expect(scaleBarLength(0.4)).toBe(200) // 80 px
    expect(scaleBarLength(1)).toBe(100)
    expect(scaleBarLength(0.05)).toBe(2000)
    expect(scaleBarLength(20)).toBe(5)
  })
})

describe('gridSteps', () => {
  it('shows the editor grid when its lines are far enough apart, with metre lines', () => {
    expect(gridSteps(50, 0.4)).toEqual({ minor: 50, major: 100 })
    expect(gridSteps(10, 1)).toEqual({ minor: 10, major: 100 })
  })

  it('thins the grid out when zoomed out (lines at least 8 px apart)', () => {
    // 50 cm at 0.1 px/cm = 5 px: doubled to 100 cm (10 px).
    expect(gridSteps(50, 0.1)).toEqual({ minor: 100, major: 500 })
    expect(gridSteps(25, 0.05).minor * 0.05).toBeGreaterThanOrEqual(8)
  })

  it('uses five minor steps per major line when a metre is not a multiple', () => {
    expect(gridSteps(30, 1)).toEqual({ minor: 30, major: 150 })
  })
})
