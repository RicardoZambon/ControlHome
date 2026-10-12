import type { FloorBackgroundDto } from '@controlhome/api-client'
import type { Rect } from '../../../../components/floorplan/usePanZoom'

/**
 * The Background tool: upload a plan image to trace, set its opacity, and drag it into place. Limits mirror
 * `FloorRules` (`MinCmPerPx`, `MaxCmPerPx`, `MaxBackgroundOffsetCm`).
 */
export const MIN_CM_PER_PX = 0.001
export const MAX_CM_PER_PX = 1000
export const MAX_OFFSET_CM = 1_000_000
export const DEFAULT_BACKGROUND_OPACITY = 0.6

export interface ImageSize {
  widthPx: number
  heightPx: number
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

/** A newly uploaded image: as large as fits on the floor, centred. */
export function fitBackground(
  fileId: string,
  image: ImageSize,
  floor: { widthCm: number; heightCm: number },
): FloorBackgroundDto {
  const cmPerPx = clamp(
    Math.min(floor.widthCm / Math.max(image.widthPx, 1), floor.heightCm / Math.max(image.heightPx, 1)),
    MIN_CM_PER_PX,
    MAX_CM_PER_PX,
  )
  return {
    fileId,
    opacity: DEFAULT_BACKGROUND_OPACITY,
    offsetXCm: (floor.widthCm - image.widthPx * cmPerPx) / 2,
    offsetYCm: (floor.heightCm - image.heightPx * cmPerPx) / 2,
    cmPerPx,
  }
}

export function moveBackground(background: FloorBackgroundDto, dx: number, dy: number): FloorBackgroundDto {
  return {
    ...background,
    offsetXCm: clamp(background.offsetXCm + dx, -MAX_OFFSET_CM, MAX_OFFSET_CM),
    offsetYCm: clamp(background.offsetYCm + dy, -MAX_OFFSET_CM, MAX_OFFSET_CM),
  }
}

/** Where the image lies on the plan, in cm. */
export function imageRect(background: FloorBackgroundDto, image: ImageSize): Rect {
  return {
    x: background.offsetXCm,
    y: background.offsetYCm,
    width: image.widthPx * background.cmPerPx,
    height: image.heightPx * background.cmPerPx,
  }
}
