import type { FloorBackgroundDto } from '@controlhome/api-client'
import { distance, type Point } from '../../../../components/floorplan/geometry'
import { MAX_CM_PER_PX, MIN_CM_PER_PX } from './backgroundTool'

/**
 * The Calibrate tool: click two points on the background image whose real distance you know (a wall, a door), enter
 * that distance, and the image is rescaled so the plan's centimetres match it.
 */
export interface CalibrationState {
  a: Point | null
  b: Point | null
}

export const emptyCalibration = (): CalibrationState => ({ a: null, b: null })

/** The first click sets `a`, the second `b`; a third starts a new measurement. */
export function addCalibrationPoint(state: CalibrationState, point: Point): CalibrationState {
  if (!state.a) return { a: point, b: null }
  if (!state.b) return { a: state.a, b: point }
  return { a: point, b: null }
}

/**
 * The background rescaled so that `a` and `b` (plan cm, as the image is drawn now) are `realCm` apart, with the image
 * pixel under `a` staying put. `null` when the distance or the resulting scale is not usable.
 */
export function calibrateBackground(
  background: FloorBackgroundDto,
  a: Point,
  b: Point,
  realCm: number,
): FloorBackgroundDto | null {
  const measured = distance(a, b)
  if (!(realCm > 0) || !(measured > 1e-6)) return null
  const factor = realCm / measured
  const cmPerPx = background.cmPerPx * factor
  if (!(cmPerPx >= MIN_CM_PER_PX && cmPerPx <= MAX_CM_PER_PX)) return null
  return {
    ...background,
    cmPerPx,
    offsetXCm: a.x + (background.offsetXCm - a.x) * factor,
    offsetYCm: a.y + (background.offsetYCm - a.y) * factor,
  }
}
