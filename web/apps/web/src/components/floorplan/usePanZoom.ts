import { useElementSize } from '@vueuse/core'
import { computed, shallowRef, toValue, type MaybeRefOrGetter, type Ref } from 'vue'
import type { Point } from './geometry'

/**
 * What part of a floor plan the canvas shows: the centimetre point at the centre of the container and the scale in
 * screen pixels per centimetre. The SVG's viewBox follows from it and the container size, with the same aspect ratio
 * as the container, so one screen pixel is always `1 / scale` cm on both axes.
 */
export interface View {
  cx: number
  cy: number
  scale: number
}

export interface Size {
  width: number
  height: number
}

export interface Rect {
  x: number
  y: number
  width: number
  height: number
}

export interface ScaleLimits {
  min: number
  max: number
}

/** The SVG viewBox (in cm) for `view` in a container of `size` px. */
export function viewBoxOf(view: View, size: Size): Rect {
  const width = size.width / view.scale
  const height = size.height / view.scale
  return { x: view.cx - width / 2, y: view.cy - height / 2, width, height }
}

/** The view that shows all of `content` centred, `padding` px from the container's edges. */
export function fitView(content: Rect, size: Size, padding: number): View {
  const availableWidth = Math.max(size.width - 2 * padding, 1)
  const availableHeight = Math.max(size.height - 2 * padding, 1)
  const scale = Math.min(
    availableWidth / Math.max(content.width, 1),
    availableHeight / Math.max(content.height, 1),
  )
  return { cx: content.x + content.width / 2, cy: content.y + content.height / 2, scale }
}

/** A point in container pixels (from its top-left corner) → floor centimetres. */
export function screenToCm(view: View, size: Size, point: Point): Point {
  return {
    x: view.cx + (point.x - size.width / 2) / view.scale,
    y: view.cy + (point.y - size.height / 2) / view.scale,
  }
}

/** Floor centimetres → container pixels. */
export function cmToScreen(view: View, size: Size, point: Point): Point {
  return {
    x: size.width / 2 + (point.x - view.cx) * view.scale,
    y: size.height / 2 + (point.y - view.cy) * view.scale,
  }
}

const clamp = (value: number, limits: ScaleLimits) => Math.min(limits.max, Math.max(limits.min, value))

/** Zooms by `factor` keeping the centimetre point under `anchor` (container px) where it is. */
export function zoomAt(view: View, factor: number, anchor: Point, size: Size, limits: ScaleLimits): View {
  const scale = clamp(view.scale * factor, limits)
  const fixed = screenToCm(view, size, anchor)
  return {
    scale,
    cx: fixed.x - (anchor.x - size.width / 2) / scale,
    cy: fixed.y - (anchor.y - size.height / 2) / scale,
  }
}

/** Moves the plan with a pointer that moved `dx`, `dy` px. */
export function panBy(view: View, dx: number, dy: number): View {
  return { ...view, cx: view.cx - dx / view.scale, cy: view.cy - dy / view.scale }
}

const midpoint = (pair: readonly [Point, Point]): Point => ({
  x: (pair[0].x + pair[1].x) / 2,
  y: (pair[0].y + pair[1].y) / 2,
})
const spread = (pair: readonly [Point, Point]) =>
  Math.max(Math.hypot(pair[1].x - pair[0].x, pair[1].y - pair[0].y), 1)

/**
 * Two fingers moved from `before` to `after` (container px): the plan scales by the change of their distance around
 * their midpoint and follows the midpoint.
 */
export function pinchView(
  view: View,
  before: readonly [Point, Point],
  after: readonly [Point, Point],
  size: Size,
  limits: ScaleLimits,
): View {
  const from = midpoint(before)
  const to = midpoint(after)
  const zoomed = zoomAt(view, spread(after) / spread(before), from, size, limits)
  return panBy(zoomed, to.x - from.x, to.y - from.y)
}

/** Zooming out stops at a quarter of the fitted scale, zooming in at 8 px per cm (or 4 × the fit for tiny floors). */
export function scaleLimits(fitScale: number): ScaleLimits {
  return { min: fitScale / 4, max: Math.max(8, fitScale * 4) }
}

/** Minimum distance between drawn grid lines, in screen px. */
const MIN_GRID_SPACING_PX = 8

/**
 * The grid to draw at `scale`: the editor grid (`gridCm`), doubled until its lines are at least 8 px apart, and heavier
 * lines every metre (or every five minor lines when a metre is not a multiple of them).
 */
export function gridSteps(gridCm: number, scale: number): { minor: number; major: number } {
  let minor = Math.max(gridCm, 1)
  while (minor * scale < MIN_GRID_SPACING_PX) minor *= 2
  const major = minor < 100 && 100 % minor === 0 ? 100 : minor * 5
  return { minor, major }
}

const SCALE_BAR_LENGTHS_CM = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000, 50_000]

/** The scale bar: the longest round length (cm) that is at most 120 px long at `scale`. */
export function scaleBarLength(scale: number, maxPx = 120): number {
  let best = SCALE_BAR_LENGTHS_CM[0]!
  for (const length of SCALE_BAR_LENGTHS_CM) if (length * scale <= maxPx) best = length
  return best
}

export interface PanZoomOptions {
  /** The element the SVG fills. */
  container: Ref<HTMLElement | null | undefined>
  /** The floor's rectangle in cm (what "fit" shows). */
  content: MaybeRefOrGetter<Rect>
  /** Space around the fitted content, px. */
  padding?: MaybeRefOrGetter<number>
}

/**
 * Pan and zoom state of a floor-plan canvas. Until the user pans or zooms, the view follows the fitted view (so it
 * re-fits when the container or the floor changes size); `fit()` returns to that.
 */
export function usePanZoom(options: PanZoomOptions) {
  const { width, height } = useElementSize(options.container)
  const size = computed<Size>(() => ({ width: width.value, height: height.value }))
  const hasSize = computed(() => size.value.width > 0 && size.value.height > 0)
  const custom = shallowRef<View | null>(null)

  const fitted = computed(() => fitView(toValue(options.content), size.value, toValue(options.padding) ?? 24))
  const view = computed(() => custom.value ?? fitted.value)
  const limits = computed(() => scaleLimits(fitted.value.scale))
  const viewBox = computed<Rect>(() =>
    hasSize.value ? viewBoxOf(view.value, size.value) : { ...toValue(options.content) },
  )
  const scale = computed(() => view.value.scale)
  const isFitted = computed(() => custom.value === null)

  const center = () => ({ x: size.value.width / 2, y: size.value.height / 2 })

  function fit(): void {
    custom.value = null
  }

  function zoomBy(factor: number, anchor: Point = center()): void {
    custom.value = zoomAt(view.value, factor, anchor, size.value, limits.value)
  }

  function pan(dx: number, dy: number): void {
    if (dx === 0 && dy === 0) return
    custom.value = panBy(view.value, dx, dy)
  }

  function pinch(before: readonly [Point, Point], after: readonly [Point, Point]): void {
    custom.value = pinchView(view.value, before, after, size.value, limits.value)
  }

  /** Shows `rect` (cm) as large as it fits, within the zoom limits (a room tapped in a list, a group of markers). */
  function zoomTo(rect: Rect): void {
    const target = fitView(rect, size.value, toValue(options.padding) ?? 24)
    custom.value = { ...target, scale: Math.min(limits.value.max, Math.max(limits.value.min, target.scale)) }
  }

  /** Viewport coordinates (e.g. `event.clientX/Y`) → container px. */
  function toLocal(clientX: number, clientY: number): Point {
    const rect = options.container.value?.getBoundingClientRect()
    return { x: clientX - (rect?.left ?? 0), y: clientY - (rect?.top ?? 0) }
  }

  /** Viewport coordinates → floor centimetres. */
  function toCm(clientX: number, clientY: number): Point {
    return screenToCm(view.value, size.value, toLocal(clientX, clientY))
  }

  return { size, view, viewBox, scale, limits, isFitted, fit, zoomBy, zoomTo, pan, pinch, toLocal, toCm }
}

export type PanZoom = ReturnType<typeof usePanZoom>
