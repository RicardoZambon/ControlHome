import type { FloorDto } from '@controlhome/api-client'
import { describe, expect, it, vi } from 'vitest'
import type { CanvasPointer } from '../../../components/floorplan/FloorPlanCanvas.vue'
import type { Point } from '../../../components/floorplan/geometry'
import { useEditorPointer } from './useEditorPointer'
import { useEditorState } from './useEditorState'

// The editor's pointer wiring, driven like the canvas drives it, at 1 px per cm (so px and cm coincide).

const P = (x: number, y: number): Point => ({ x, y })
const square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

function setup(rooms: FloorDto['rooms'] = []) {
  const state = useEditorState({ roomBaseName: () => 'Room' })
  state.load({
    id: 1,
    name: 'Ground',
    level: 0,
    widthCm: 2000,
    heightCm: 1500,
    gridCm: 50,
    background: { fileId: 'f', opacity: 0.5, offsetXCm: 0, offsetYCm: 0, cmPerPx: 2 },
    rooms,
    rowVersion: 'v1',
  })
  const onCalibrationPoints = vi.fn()
  const pointer = useEditorPointer(state, { scale: () => 1, coarse: () => false, onCalibrationPoints })
  const at = (x: number, y: number, mods: { altKey?: boolean } = {}): CanvasPointer => ({
    cm: P(x, y),
    px: P(x, y),
    event: { altKey: false, ...mods } as PointerEvent,
  })
  const click = (x: number, y: number) => {
    const p = at(x, y)
    const taken = pointer.handler.down!(p)
    if (taken) pointer.handler.up!(p)
    else pointer.handler.tap!(p)
  }
  const drag = (from: Point, to: Point, mods: { altKey?: boolean } = {}) => {
    expect(pointer.handler.down!(at(from.x, from.y, mods))).toBe(true)
    const steps = 4
    for (let i = 1; i <= steps; i++) {
      pointer.handler.move!(
        at(from.x + ((to.x - from.x) * i) / steps, from.y + ((to.y - from.y) * i) / steps, mods),
      )
    }
    pointer.handler.up!(at(to.x, to.y, mods))
  }
  return { state, pointer, at, click, drag, onCalibrationPoints }
}

const room = (id: number, polygon: Point[]) => ({
  id,
  name: `R${id}`,
  icon: 'house',
  colorToken: 'room-1',
  polygon,
  areaM2: 0,
  sortOrder: id,
})

describe('drawing', () => {
  it('clicks place corners (snapped to the grid) and a click on the first corner closes the room', () => {
    const { state, click } = setup()
    state.setTool('draw')
    click(102, 98)
    click(503, 101)
    click(498, 400)
    click(100, 403)
    expect(state.drawing.value.points).toEqual([P(100, 100), P(500, 100), P(500, 400), P(100, 400)])
    click(104, 97)
    expect(state.rooms.value).toHaveLength(1)
    expect(state.rooms.value[0]!.polygon).toEqual([P(100, 100), P(500, 100), P(500, 400), P(100, 400)])
    expect(state.tool.value).toBe('select')
    expect(state.selection.value.roomKey).toBe(state.rooms.value[0]!.key)
  })

  it('Enter (finishDrawing) closes the outline once there are three corners', () => {
    const { state, click, pointer } = setup()
    state.setTool('draw')
    click(100, 100)
    click(300, 100)
    expect(pointer.finishDrawing()).toBe(false)
    click(300, 300)
    expect(pointer.finishDrawing()).toBe(true)
    expect(state.rooms.value[0]!.polygon).toHaveLength(3)
  })

  it('dragging on the empty plan draws a rectangle', () => {
    const { state, drag } = setup()
    state.setTool('draw')
    drag(P(200, 200), P(601, 448))
    expect(state.rooms.value[0]!.polygon).toEqual([P(200, 200), P(600, 200), P(600, 450), P(200, 450)])
  })

  it('Esc (cancelTool) drops the corners placed so far', () => {
    const { state, click, pointer } = setup()
    state.setTool('draw')
    click(100, 100)
    expect(pointer.cancelTool()).toBe(true)
    expect(state.drawing.value.points).toEqual([])
    expect(pointer.cancelTool()).toBe(false)
  })
})

describe('select tool', () => {
  it('a click selects a room, a click on the empty plan clears the selection', () => {
    const { state, click } = setup([room(1, square(0, 0, 400))])
    click(200, 200)
    expect(state.selection.value.roomKey).toBe('room-1')
    click(1500, 1000)
    expect(state.selection.value.roomKey).toBeNull()
  })

  it('dragging a room moves it in one undo step, snapping to the grid; Alt moves it freely', () => {
    const { state, drag } = setup([room(1, square(0, 0, 400))])
    drag(P(200, 200), P(303, 148))
    expect(state.rooms.value[0]!.polygon[0]).toEqual(P(100, 0))
    state.undo()
    expect(state.rooms.value[0]!.polygon[0]).toEqual(P(0, 0))
    drag(P(200, 200), P(303, 248), { altKey: true })
    expect(state.rooms.value[0]!.polygon[0]).toEqual(P(103, 48))
  })

  it('a room cannot be dragged off the floor', () => {
    const { state, drag } = setup([room(1, square(0, 0, 400))])
    drag(P(200, 200), P(-500, -500))
    expect(state.rooms.value[0]!.polygon[0]).toEqual(P(0, 0))
  })

  it('dragging a corner of the selected room reshapes it', () => {
    const { state, click, drag } = setup([room(1, square(0, 0, 400))])
    click(200, 200)
    drag(P(400, 400), P(551, 448))
    expect(state.rooms.value[0]!.polygon[2]).toEqual(P(550, 450))
    expect(state.selection.value).toEqual({ roomKey: 'room-1', vertex: 2 })
  })

  it('dragging a wall midpoint adds a corner there', () => {
    const { state, click, drag } = setup([room(1, square(0, 0, 400))])
    click(200, 200)
    drag(P(200, 0), P(200, -100))
    // Clamped to the floor: the new corner stays at y = 0, but it exists.
    expect(state.rooms.value[0]!.polygon).toHaveLength(5)
    expect(state.rooms.value[0]!.polygon[1]).toEqual(P(200, 0))
  })

  it('double-clicking a wall of the selected room adds a corner', () => {
    const { state, click, at, pointer } = setup([room(1, square(0, 0, 400))])
    click(200, 200)
    pointer.handler.dblclick!(at(400, 103))
    expect(state.rooms.value[0]!.polygon).toEqual([P(0, 0), P(400, 0), P(400, 103), P(400, 400), P(0, 400)])
    expect(state.selection.value.vertex).toBe(2)
  })
})

describe('calibrate and background tools', () => {
  it('two taps ask for the real distance', () => {
    const { state, click, onCalibrationPoints } = setup()
    state.setTool('calibrate')
    click(100, 100)
    click(350, 100)
    expect(onCalibrationPoints).toHaveBeenCalledWith(P(100, 100), P(350, 100))
  })

  it('dragging moves the background', () => {
    const { state, drag } = setup()
    state.setTool('background')
    drag(P(500, 500), P(530, 480))
    expect(state.draft.value.background).toMatchObject({ offsetXCm: 30, offsetYCm: -20 })
    state.undo()
    expect(state.draft.value.background).toMatchObject({ offsetXCm: 0, offsetYCm: 0 })
  })
})
