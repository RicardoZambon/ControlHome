import type { FloorBackgroundDto } from '@controlhome/api-client'
import { computed, ref, shallowRef } from 'vue'
import type { CanvasPointer, CanvasPointerHandler } from '../../../components/floorplan/FloorPlanCanvas.vue'
import { areaCm2, type Point } from '../../../components/floorplan/geometry'
import { moveBackground } from './tools/backgroundTool'
import { addCalibrationPoint } from './tools/calibrateTool'
import { addDrawPoint, canCloseDraw, emptyDraw, rectanglePolygon } from './tools/drawTool'
import {
  clampMove,
  clampToFloor,
  exceedsDragThreshold,
  hitTest,
  insertVertex,
  midpointHit,
  moveVertex,
  translatePolygon,
  type Hit,
} from './tools/selectTool'
import type { EditorState } from './useEditorState'
import { snapContext, useSnapping, type SnapKind } from './useSnapping'

type Gesture =
  | { kind: 'vertex'; key: string; index: number; original: Point[] }
  | { kind: 'insert'; key: string; edge: number; point: Point; startPx: Point; original: Point[] }
  | { kind: 'room'; key: string; start: Point; startPx: Point; original: Point[]; moved: boolean }
  | { kind: 'draw'; start: Point; startPx: Point }
  | { kind: 'rectangle'; start: Point }
  | { kind: 'background'; start: Point; original: FloorBackgroundDto }

export interface EditorPointerOptions {
  /** The canvas scale (screen px per cm). */
  scale: () => number
  /** Touch screens get bigger handles. */
  coarse: () => boolean
  /** Both calibration points are placed: ask for the real distance. */
  onCalibrationPoints: (a: Point, b: Point) => void
}

/** Visible corner handles (radius, px) and how far around them a press grabs them. */
export const HANDLE_RADIUS_PX = { fine: 5, coarse: 7 }
const GRAB_RADIUS_PX = { fine: 10, coarse: 22 }
/** Walls shorter than this on screen get no midpoint handle (it would crowd the corners). */
export const MIDPOINT_MIN_WALL_PX = 72

/**
 * Turns canvas pointer input into editor actions for the active tool (see `tools/*`): select / move / reshape rooms,
 * draw outlines (clicks or a dragged rectangle), place calibration points, drag the background. Also tracks what is
 * under the pointer (hover, snapped cursor) for the overlay.
 */
export function useEditorPointer(state: EditorState, options: EditorPointerOptions) {
  const snapping = useSnapping(12)
  /** The snapped pointer position (cm) while drawing or calibrating; null when outside the canvas. */
  const cursor = ref<Point | null>(null)
  const snapKind = ref<SnapKind | null>(null)
  const hover = shallowRef<Hit | { kind: 'midpoint'; roomKey: string; index: number }>({ kind: 'none' })
  const rectangle = ref<{ a: Point; b: Point } | null>(null)
  let gesture: Gesture | null = null

  const scale = () => options.scale()
  const grab = () => (options.coarse() ? GRAB_RADIUS_PX.coarse : GRAB_RADIUS_PX.fine) / scale()
  const floor = () => state.draft.value.floor

  function context(excludeKey: string | null, ownVertices: readonly Point[] = []) {
    return snapContext(state.rooms.value, { gridCm: floor().gridCm, excludeKey, ownVertices })
  }

  /** A drawing point: on the floor, snapped to the grid, the other rooms and the corners placed so far. */
  function snapDrawPoint(pointer: CanvasPointer): Point {
    const onFloor = clampToFloor(pointer.cm, floor().widthCm, floor().heightCm)
    const snapped = snapping.snap(
      onFloor,
      context(null, state.drawing.value.points),
      scale(),
      pointer.event.altKey,
    )
    snapKind.value = snapped.kind
    return clampToFloor(snapped.point, floor().widthCm, floor().heightCm)
  }

  /** A wall-midpoint handle of the selected room under the pointer (shown only on walls long enough on screen). */
  function midpointUnder(polygon: readonly Point[], point: Point) {
    const middle = midpointHit(polygon, point, grab() * 0.8)
    if (!middle) return null
    const a = polygon[middle.index]!
    const b = polygon[(middle.index + 1) % polygon.length]!
    return Math.hypot(b.x - a.x, b.y - a.y) * scale() >= MIDPOINT_MIN_WALL_PX ? middle : null
  }

  // --- Select ---------------------------------------------------------------------------------------------------------
  function selectDown(pointer: CanvasPointer): boolean {
    const selected = state.selectedRoom.value
    const hit = hitTest(state.rooms.value, pointer.cm, grab(), selected?.key ?? null)
    if (hit.kind === 'vertex') {
      const room = state.rooms.value.find((entry) => entry.key === hit.roomKey)!
      state.select(hit.roomKey, hit.index)
      state.preview.begin()
      gesture = { kind: 'vertex', key: hit.roomKey, index: hit.index, original: room.polygon }
      return true
    }
    if (selected) {
      const middle = midpointUnder(selected.polygon, pointer.cm)
      if (middle) {
        state.preview.begin()
        gesture = {
          kind: 'insert',
          key: selected.key,
          edge: middle.index,
          point: middle.point,
          startPx: pointer.px,
          original: selected.polygon,
        }
        return true
      }
    }
    if (hit.kind === 'room' || hit.kind === 'edge') {
      const room = state.rooms.value.find((entry) => entry.key === hit.roomKey)!
      if (state.selection.value.roomKey !== hit.roomKey || state.selection.value.vertex !== null) {
        state.select(hit.roomKey)
      }
      state.preview.begin()
      gesture = {
        kind: 'room',
        key: hit.roomKey,
        start: pointer.cm,
        startPx: pointer.px,
        original: room.polygon,
        moved: false,
      }
      return true
    }
    return false
  }

  function dragVertex(pointer: CanvasPointer, key: string, index: number, original: Point[]): void {
    const { widthCm, heightCm } = floor()
    const target = clampToFloor(pointer.cm, widthCm, heightCm)
    const snapped = snapping.snap(target, context(key), scale(), pointer.event.altKey)
    snapKind.value = snapped.kind
    state.preview.room(key, moveVertex(original, index, clampToFloor(snapped.point, widthCm, heightCm)))
  }

  function selectMove(pointer: CanvasPointer): void {
    const current = gesture
    if (current?.kind === 'vertex') {
      dragVertex(pointer, current.key, current.index, current.original)
      return
    }
    if (current?.kind === 'insert') {
      if (!exceedsDragThreshold(current.startPx, pointer.px)) return
      const polygon = insertVertex(current.original, current.edge, current.point)
      gesture = { kind: 'vertex', key: current.key, index: current.edge + 1, original: polygon }
      state.select(current.key, current.edge + 1)
      dragVertex(pointer, current.key, current.edge + 1, polygon)
      return
    }
    if (current?.kind === 'room') {
      if (!current.moved && !exceedsDragThreshold(current.startPx, pointer.px)) return
      current.moved = true
      const dx = pointer.cm.x - current.start.x
      const dy = pointer.cm.y - current.start.y
      const moved = translatePolygon(current.original, dx, dy)
      const snap = snapping.snapMove(moved, context(current.key), scale(), pointer.event.altKey)
      snapKind.value = snap.kind
      const { widthCm, heightCm } = floor()
      const clamped = clampMove(current.original, dx + snap.dx, dy + snap.dy, widthCm, heightCm)
      state.preview.room(current.key, translatePolygon(current.original, clamped.dx, clamped.dy))
      return
    }
    // Hover: what a press would grab.
    const selected = state.selectedRoom.value
    let hit: typeof hover.value = hitTest(state.rooms.value, pointer.cm, grab(), selected?.key ?? null)
    if (selected && hit.kind !== 'vertex') {
      const middle = midpointUnder(selected.polygon, pointer.cm)
      if (middle) hit = { kind: 'midpoint', roomKey: selected.key, index: middle.index }
    }
    hover.value = hit
    state.hoverRoomKey.value = hit.kind === 'none' ? null : hit.roomKey
  }

  function selectUp(): void {
    const current = gesture
    if (current?.kind === 'vertex') state.preview.commit('floors.editor.history.moveCorner')
    else if (current?.kind === 'room') {
      if (current.moved) state.preview.commit('floors.editor.history.moveRoom')
      else state.preview.rollback()
    } else if (current?.kind === 'insert') {
      // A click on a midpoint handle without dragging only selects.
      state.preview.rollback()
    }
    snapKind.value = null
  }

  // --- Draw -----------------------------------------------------------------------------------------------------------
  function finishDrawing(): boolean {
    const drawing = state.drawing.value
    if (!canCloseDraw(drawing)) return false
    state.addRoom(drawing.points)
    state.setTool('select')
    return true
  }

  function drawDown(pointer: CanvasPointer): boolean {
    const point = snapDrawPoint(pointer)
    cursor.value = point
    gesture = { kind: 'draw', start: point, startPx: pointer.px }
    return true
  }

  function drawMove(pointer: CanvasPointer): void {
    const point = snapDrawPoint(pointer)
    cursor.value = point
    const current = gesture
    if (
      current?.kind === 'draw' &&
      state.drawing.value.points.length === 0 &&
      exceedsDragThreshold(current.startPx, pointer.px)
    ) {
      gesture = { kind: 'rectangle', start: current.start }
    }
    if (gesture?.kind === 'rectangle') rectangle.value = { a: gesture.start, b: point }
  }

  function drawUp(): void {
    const current = gesture
    if (current?.kind === 'rectangle' && rectangle.value) {
      const polygon = rectanglePolygon(rectangle.value.a, rectangle.value.b)
      rectangle.value = null
      if (areaCm2(polygon) >= 1) {
        state.addRoom(polygon)
        state.setTool('select')
      }
      return
    }
    if (current?.kind !== 'draw') return
    const result = addDrawPoint(state.drawing.value, current.start, grab())
    state.drawing.value = result.state
    if (result.closed) {
      state.addRoom(result.closed)
      state.setTool('select')
    }
  }

  // --- Background -----------------------------------------------------------------------------------------------------
  function backgroundDown(pointer: CanvasPointer): boolean {
    const background = state.draft.value.background
    if (!background) return false
    state.preview.begin()
    gesture = { kind: 'background', start: pointer.cm, original: background }
    return true
  }

  function backgroundMove(pointer: CanvasPointer): void {
    const current = gesture
    if (current?.kind !== 'background') return
    state.preview.background(
      moveBackground(current.original, pointer.cm.x - current.start.x, pointer.cm.y - current.start.y),
    )
  }

  // --- The handler the canvas calls -------------------------------------------------------------------------------
  const handler: CanvasPointerHandler = {
    down(pointer) {
      gesture = null
      switch (state.tool.value) {
        case 'select':
          return selectDown(pointer)
        case 'draw':
          return drawDown(pointer)
        case 'background':
          return backgroundDown(pointer)
        default:
          return false
      }
    },
    move(pointer) {
      switch (state.tool.value) {
        case 'select':
          selectMove(pointer)
          break
        case 'draw':
          drawMove(pointer)
          break
        case 'calibrate':
          cursor.value = pointer.cm
          break
        case 'background':
          backgroundMove(pointer)
          break
      }
    },
    up() {
      switch (state.tool.value) {
        case 'select':
          selectUp()
          break
        case 'draw':
          drawUp()
          break
        case 'background':
          if (gesture?.kind === 'background') state.preview.commit('floors.editor.history.moveBackground')
          break
      }
      gesture = null
    },
    cancel() {
      if (gesture && gesture.kind !== 'draw' && gesture.kind !== 'rectangle') state.preview.rollback()
      rectangle.value = null
      gesture = null
    },
    tap(pointer) {
      if (state.tool.value === 'calibrate') {
        const next = addCalibrationPoint(state.calibration.value, pointer.cm)
        state.calibration.value = next
        if (next.a && next.b) options.onCalibrationPoints(next.a, next.b)
        return true
      }
      if (state.tool.value === 'select') {
        // A click on the empty plan clears the selection.
        state.select(null)
        return true
      }
      return false
    },
    dblclick(pointer) {
      if (state.tool.value === 'draw') {
        finishDrawing()
        return
      }
      if (state.tool.value !== 'select') return
      const selected = state.selectedRoom.value
      if (!selected) return
      const hit = hitTest(state.rooms.value, pointer.cm, grab(), selected.key)
      if (hit.kind !== 'edge') return
      state.updateRoom(
        selected.key,
        { polygon: insertVertex(selected.polygon, hit.index, hit.point) },
        'floors.editor.history.addCorner',
      )
      state.select(selected.key, hit.index + 1)
    },
    leave() {
      cursor.value = null
      snapKind.value = null
      hover.value = { kind: 'none' }
      state.hoverRoomKey.value = null
    },
  }

  /** Cancels whatever the active tool is in the middle of (Esc). Returns whether there was anything. */
  function cancelTool(): boolean {
    if (state.tool.value === 'draw' && state.drawing.value.points.length > 0) {
      state.drawing.value = emptyDraw()
      return true
    }
    if (state.tool.value === 'calibrate' && state.calibration.value.a) {
      state.calibration.value = { a: null, b: null }
      return true
    }
    return false
  }

  const cursorClass = computed(() => {
    switch (state.tool.value) {
      case 'draw':
      case 'calibrate':
        return 'cursor-crosshair'
      case 'background':
        return state.draft.value.background ? 'cursor-move' : 'cursor-not-allowed'
      default: {
        const hit = hover.value
        if (hit.kind === 'vertex' || hit.kind === 'midpoint') return 'cursor-pointer'
        if (hit.kind === 'room' || hit.kind === 'edge') return 'cursor-move'
        return 'cursor-grab'
      }
    }
  })

  return {
    handler,
    cursor,
    snapKind,
    hover,
    rectangle,
    cursorClass,
    finishDrawing,
    cancelTool,
  }
}

export type EditorPointer = ReturnType<typeof useEditorPointer>
