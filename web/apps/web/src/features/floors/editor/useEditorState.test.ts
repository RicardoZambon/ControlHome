import type { FloorDto, RoomDto } from '@controlhome/api-client'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Point } from '../../../components/floorplan/geometry'

const api = vi.hoisted(() => ({ updateFloor: vi.fn(), saveFloorLayout: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const { useEditorState } = await import('./useEditorState')

const P = (x: number, y: number): Point => ({ x, y })
const square = (x: number, y: number, size: number): Point[] => [
  P(x, y),
  P(x + size, y),
  P(x + size, y + size),
  P(x, y + size),
]

const living: RoomDto = {
  id: 11,
  name: 'Living',
  icon: 'sofa',
  colorToken: 'room-1',
  polygon: square(0, 0, 400),
  areaM2: 16,
  sortOrder: 0,
}

function floor(patch: Partial<FloorDto> = {}): FloorDto {
  return {
    id: 7,
    name: 'Ground',
    level: 0,
    widthCm: 2000,
    heightCm: 1500,
    gridCm: 50,
    background: null,
    rooms: [living],
    rowVersion: 'v1',
    placements: [],
    ...patch,
  }
}

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const problem = (status: number, body: unknown) => ({
  data: undefined,
  error: body,
  response: new Response(null, { status }),
})

/** The layout endpoint as the server answers: the sent rooms with ids (new ones numbered from 100). */
function echoLayout(version: string) {
  let next = 100
  return ({ body }: { body: { rooms: { id: number | null; name: string; polygon: Point[] }[] } }) =>
    Promise.resolve(
      ok(
        floor({
          rowVersion: version,
          rooms: body.rooms.map((room, index) => ({
            ...living,
            id: room.id ?? next++,
            name: room.name,
            polygon: room.polygon,
            sortOrder: index,
          })),
        }),
      ),
    )
}

function editor() {
  const state = useEditorState({ roomBaseName: () => 'Room' })
  state.load(floor())
  return state
}

beforeEach(() => {
  api.updateFloor.mockReset()
  api.saveFloorLayout.mockReset()
})

describe('useEditorState', () => {
  it('saves a new room, then updates it by its new id', async () => {
    api.saveFloorLayout.mockImplementationOnce(echoLayout('v2')).mockImplementationOnce(echoLayout('v3'))
    const state = editor()
    const key = state.addRoom(square(500, 0, 300))
    expect(state.selection.value.roomKey).toBe(key)
    expect(state.draft.value.rooms[1]!.name).toBe('Room 2')
    expect(state.isDirty.value).toBe(true)

    expect(await state.save()).toEqual({ kind: 'saved' })
    expect(api.saveFloorLayout).toHaveBeenCalledWith({
      path: { id: 7 },
      body: {
        rowVersion: 'v1',
        rooms: [
          expect.objectContaining({ id: 11, name: 'Living' }),
          expect.objectContaining({ id: null, name: 'Room 2' }),
        ],
      },
    })
    expect(state.isDirty.value).toBe(false)
    expect(await state.save()).toEqual({ kind: 'nothing' })

    state.updateRoom(key, { name: 'Office' }, 'rename', `name:${key}`)
    expect(await state.save()).toEqual({ kind: 'saved' })
    expect(api.saveFloorLayout.mock.calls[1]![0].body).toMatchObject({
      rowVersion: 'v2',
      rooms: [{ id: 11 }, { id: 100, name: 'Office' }],
    })
    expect(state.rowVersion.value).toBe('v3')
  })

  it('undo and redo walk the history; undoing to the saved state is not dirty', () => {
    const state = editor()
    const key = state.addRoom(square(500, 0, 300))
    state.updateRoom(key, { colorToken: 'room-5' }, 'color')
    state.undo()
    expect(state.draft.value.rooms[1]!.colorToken).not.toBe('room-5')
    state.undo()
    expect(state.draft.value.rooms).toHaveLength(1)
    expect(state.selection.value.roomKey).toBeNull()
    expect(state.isDirty.value).toBe(false)
    state.redo()
    state.redo()
    expect(state.draft.value.rooms[1]!.colorToken).toBe('room-5')
  })

  it('does not send a draft that breaks the rules', async () => {
    const state = editor()
    state.addRoom([P(500, 0), P(800, 300), P(800, 0), P(500, 300)])
    expect(await state.save()).toEqual({ kind: 'invalid' })
    expect(api.saveFloorLayout).not.toHaveBeenCalled()
    expect(state.errors.value.count).toBe(1)
    expect(state.attemptedSave.value).toBe(true)
  })

  it('a stale floor is a conflict and keeps the draft', async () => {
    api.saveFloorLayout.mockResolvedValue(problem(409, { status: 409, code: 'floors.concurrency' }))
    const state = editor()
    state.addRoom(square(500, 0, 300))
    expect(await state.save()).toEqual({ kind: 'conflict' })
    expect(state.isDirty.value).toBe(true)
    expect(state.draft.value.rooms).toHaveLength(2)
  })

  it('shows the server field errors on the rooms they belong to until the next edit', async () => {
    api.saveFloorLayout.mockResolvedValue(
      problem(400, {
        status: 400,
        code: 'validation.failed',
        errors: { 'rooms[1].polygon': ['geometry.out_of_bounds'] },
      }),
    )
    const state = editor()
    const key = state.addRoom(square(500, 0, 300))
    expect(await state.save()).toEqual({ kind: 'invalid' })
    expect(state.errors.value.rooms[key]).toEqual({ polygon: { key: 'errors.geometry.out_of_bounds' } })
    expect(state.invalidRoomKeys.value).toEqual([key])

    state.updateRoom(key, { name: 'Office' }, 'rename')
    expect(state.errors.value.count).toBe(0)
  })

  it('other failures are reported as failed', async () => {
    const failure = new TypeError('Failed to fetch')
    api.saveFloorLayout.mockRejectedValue(failure)
    const state = editor()
    state.addRoom(square(500, 0, 300))
    expect(await state.save()).toEqual({ kind: 'failed', error: failure })
    expect(state.saving.value).toBe(false)
  })

  it('saves floor properties first and the layout with the new row version', async () => {
    api.updateFloor.mockResolvedValue(ok(floor({ name: 'Térreo', rowVersion: 'v2' })))
    api.saveFloorLayout.mockImplementation(echoLayout('v3'))
    const state = editor()
    state.updateFloor({ name: 'Térreo' })
    state.addRoom(square(500, 0, 300))
    expect(await state.save()).toEqual({ kind: 'saved' })
    expect(api.updateFloor).toHaveBeenCalledWith({
      path: { id: 7 },
      body: expect.objectContaining({ name: 'Térreo', rowVersion: 'v1', widthCm: 2000, heightCm: 1500 }),
    })
    expect(api.saveFloorLayout.mock.calls[0]![0].body.rowVersion).toBe('v2')
    expect(state.rowVersion.value).toBe('v3')
  })

  it('a transaction (drag) is one undo step', () => {
    const state = editor()
    const key = state.draft.value.rooms[0]!.key
    state.preview.begin()
    state.preview.room(key, square(10, 0, 400))
    state.preview.room(key, square(20, 0, 400))
    state.preview.commit('move')
    expect(state.draft.value.rooms[0]!.polygon[0]).toEqual(P(20, 0))
    state.undo()
    expect(state.draft.value.rooms[0]!.polygon[0]).toEqual(P(0, 0))
  })

  it('while drawing, Undo takes back the last corner instead of the last edit', () => {
    const state = editor()
    state.addRoom(square(500, 0, 300))
    state.setTool('draw')
    state.drawing.value = { points: [P(0, 600), P(300, 600)] }
    expect(state.canUndo.value).toBe(true)
    expect(state.undoLabel.value).toBe('floors.editor.history.placeCorner')
    state.undo()
    expect(state.drawing.value.points).toEqual([P(0, 600)])
    state.undo()
    expect(state.drawing.value.points).toEqual([])
    expect(state.rooms.value).toHaveLength(2)
    expect(state.undoLabel.value).toBe('floors.editor.history.addRoom')
    state.undo()
    expect(state.rooms.value).toHaveLength(1)
  })

  it('calibrate needs a background; choosing the draw tool clears the selection', () => {
    const state = editor()
    state.setTool('calibrate')
    expect(state.tool.value).toBe('select')
    state.select(state.draft.value.rooms[0]!.key)
    state.setTool('draw')
    expect(state.tool.value).toBe('draw')
    expect(state.selection.value.roomKey).toBeNull()
  })

  it('a new tool forgets the hovered room (Draw never highlights whole rooms)', () => {
    const state = editor()
    state.hoverRoomKey.value = state.draft.value.rooms[0]!.key
    state.setTool('draw')
    expect(state.hoverRoomKey.value).toBeNull()
  })

  it('deleting a room hands back its name and a one-shot undo', () => {
    const state = editor()
    const key = state.draft.value.rooms[0]!.key
    state.select(key)
    const deleted = state.deleteRoom(key)
    expect(deleted?.name).toBe('Living')
    expect(state.rooms.value).toHaveLength(0)
    expect(deleted!.undo()).toBe(true)
    expect(state.rooms.value.map((room) => room.name)).toEqual(['Living'])
    expect(state.selection.value.roomKey).toBe(key)
    // Only once: the room is back.
    expect(deleted!.undo()).toBe(false)
    expect(state.rooms.value).toHaveLength(1)
  })

  it("the delete's undo does nothing once something else was edited", () => {
    const state = editor()
    const other = state.addRoom(square(500, 0, 300))
    const deleted = state.deleteRoom(state.draft.value.rooms[0]!.key)
    state.updateRoom(other, { name: 'Office' }, 'rename')
    expect(deleted!.undo()).toBe(false)
    expect(state.rooms.value.map((room) => room.name)).toEqual(['Office'])
    expect(state.deleteRoom('no-such-room')).toBeNull()
  })
})
