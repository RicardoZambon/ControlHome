import type { FloorBackgroundDto } from '@controlhome/api-client'
import { describe, expect, it } from 'vitest'
import type { Point } from '../../../../components/floorplan/geometry'
import { fitBackground, imageRect, moveBackground } from './backgroundTool'
import { addCalibrationPoint, calibrateBackground, emptyCalibration } from './calibrateTool'

const P = (x: number, y: number): Point => ({ x, y })
const background = (patch: Partial<FloorBackgroundDto> = {}): FloorBackgroundDto => ({
  fileId: '0b0e7a8e-0000-4000-8000-000000000001',
  opacity: 0.6,
  offsetXCm: 100,
  offsetYCm: 50,
  cmPerPx: 2,
  ...patch,
})

describe('calibrate tool', () => {
  it('takes two points, a third click starts over', () => {
    let state = emptyCalibration()
    state = addCalibrationPoint(state, P(1, 2))
    expect(state).toEqual({ a: P(1, 2), b: null })
    state = addCalibrationPoint(state, P(3, 4))
    expect(state).toEqual({ a: P(1, 2), b: P(3, 4) })
    expect(addCalibrationPoint(state, P(5, 6))).toEqual({ a: P(5, 6), b: null })
  })

  it('scales the image so the two points are the real distance apart, keeping the first point in place', () => {
    const a = P(200, 100)
    const b = P(450, 100) // 250 cm apart on the plan as drawn now
    const calibrated = calibrateBackground(background(), a, b, 500)!
    expect(calibrated.cmPerPx).toBeCloseTo(4, 9)
    expect(calibrated.offsetXCm).toBeCloseTo(0, 9)
    expect(calibrated.offsetYCm).toBeCloseTo(0, 9)
    expect(calibrated.fileId).toBe(background().fileId)
    expect(calibrated.opacity).toBe(0.6)

    // The image pixel under `a` before is still under `a` after.
    const pixel = { x: (a.x - 100) / 2, y: (a.y - 50) / 2 }
    expect(calibrated.offsetXCm + pixel.x * calibrated.cmPerPx).toBeCloseTo(a.x, 9)
    expect(calibrated.offsetYCm + pixel.y * calibrated.cmPerPx).toBeCloseTo(a.y, 9)
  })

  it('refuses a zero or negative distance and points on top of each other', () => {
    expect(calibrateBackground(background(), P(0, 0), P(100, 0), 0)).toBeNull()
    expect(calibrateBackground(background(), P(0, 0), P(100, 0), -5)).toBeNull()
    expect(calibrateBackground(background(), P(10, 10), P(10, 10), 500)).toBeNull()
  })

  it('refuses a scale outside what the server accepts (0.001 to 1000 cm per pixel)', () => {
    expect(calibrateBackground(background({ cmPerPx: 900 }), P(0, 0), P(1, 0), 100)).toBeNull()
  })
})

describe('background tool', () => {
  it('fits a new image inside the floor, centred', () => {
    // 1000 × 500 px on a 2000 × 1500 cm floor: 2 cm per pixel, 2000 × 1000 cm, centred vertically.
    expect(fitBackground('id', { widthPx: 1000, heightPx: 500 }, { widthCm: 2000, heightCm: 1500 })).toEqual({
      fileId: 'id',
      opacity: 0.6,
      offsetXCm: 0,
      offsetYCm: 250,
      cmPerPx: 2,
    })
  })

  it('moves the image', () => {
    expect(moveBackground(background(), 10, -20)).toMatchObject({ offsetXCm: 110, offsetYCm: 30 })
  })

  it('imageRect is where the image lies on the plan', () => {
    expect(imageRect(background(), { widthPx: 300, heightPx: 200 })).toEqual({
      x: 100,
      y: 50,
      width: 600,
      height: 400,
    })
  })
})
