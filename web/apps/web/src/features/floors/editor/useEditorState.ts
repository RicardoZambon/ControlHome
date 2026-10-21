import {
  saveFloorLayout,
  updateFloor as apiUpdateFloor,
  type FloorBackgroundDto,
  type FloorDto,
} from '@controlhome/api-client'
import { computed, inject, provide, ref, shallowRef, watch, type InjectionKey } from 'vue'
import type { Point } from '../../../components/floorplan/geometry'
import { errorCodeOf, fieldErrorsOf, unwrap } from '../../../lib/problem'
import {
  DEFAULT_ROOM_ICON,
  draftFromFloor,
  errorsFromProblem,
  floorRequest,
  layoutRequest,
  newRoomKey,
  nextColorToken,
  nextRoomName,
  planSave,
  validateDraft,
  type Draft,
  type DraftErrors,
  type DraftFloor,
  type DraftRoom,
  type RoomErrors,
} from './draft'
import { emptyCalibration, type CalibrationState } from './tools/calibrateTool'
import { emptyDraw, removeLastDrawPoint, type DrawState } from './tools/drawTool'
import { useCommandStack } from './useCommandStack'

export type EditorTool = 'select' | 'draw' | 'calibrate' | 'background'
export type PanelTab = 'rooms' | 'devices' | 'floor' | 'background'

export interface EditorSelection {
  roomKey: string | null
  /** A selected corner of the selected room (Delete removes it). */
  vertex: number | null
}

/**
 * `saved`: everything is on the server. `nothing`: there was nothing to save. `invalid`: the draft (or the server)
 * found problems, now shown inline. `conflict`: someone else saved the floor (409 `floors.concurrency`). `failed`: any
 * other error (`error` holds it).
 */
export type SaveOutcome =
  { kind: 'saved' | 'nothing' | 'invalid' | 'conflict' } | { kind: 'failed'; error: unknown }

const EMPTY_DRAFT: Draft = {
  floor: { name: '', level: 0, widthCm: 2000, heightCm: 1500, gridCm: 50 },
  rooms: [],
  background: null,
}

const NO_ERRORS: DraftErrors = { rooms: {}, floor: {}, count: 0 }

function mergeErrors(a: DraftErrors, b: DraftErrors | null): DraftErrors {
  if (!b || b.count === 0) return a
  const rooms: Record<string, RoomErrors> = { ...a.rooms }
  for (const [key, errors] of Object.entries(b.rooms)) rooms[key] = { ...errors, ...rooms[key] }
  const floor = { ...b.floor, ...a.floor }
  let count = Object.keys(floor).length
  for (const errors of Object.values(rooms)) count += Object.keys(errors).length
  return { rooms, floor, count }
}

/** A room just deleted: its name, and an undo that works once and only while nothing else has changed. */
export interface DeletedRoom {
  name: string
  undo: () => boolean
}

export interface EditorStateOptions {
  /** "Room" in "Room 2", the name of a drawn room. */
  roomBaseName: () => string
  /** A room was deleted (by any part of the editor): the page offers to undo it. */
  onRoomDeleted?: (deleted: DeletedRoom) => void
}

/**
 * Everything the floor-plan editor knows: the floor as the server has it, the draft being edited (with undo/redo), the
 * active tool and its in-progress state, the selection, validation and saving. Provided by the editor page and injected
 * by its parts (`useEditor()`).
 */
export function useEditorState(options: EditorStateOptions) {
  const stack = useCommandStack<Draft>(EMPTY_DRAFT)
  const draft = stack.present
  const floorId = ref<number | null>(null)
  const server = shallowRef<FloorDto | null>(null)
  const rowVersion = ref('')
  let ids = new Map<string, number>()

  const tool = ref<EditorTool>('select')
  const selection = ref<EditorSelection>({ roomKey: null, vertex: null })
  const hoverRoomKey = ref<string | null>(null)
  const drawing = ref<DrawState>(emptyDraw())
  const calibration = ref<CalibrationState>(emptyCalibration())
  const panelTab = ref<PanelTab>('rooms')
  const backgroundSize = ref<{ widthPx: number; heightPx: number } | null>(null)

  const serverErrors = shallowRef<DraftErrors | null>(null)
  const saving = ref(false)
  /** Set by a save attempt that found problems: the panel then lists them. */
  const attemptedSave = ref(false)

  // Any edit makes the server's verdict on the previous draft stale.
  watch(draft, () => (serverErrors.value = null), { flush: 'sync' })

  const clientErrors = computed(() => (draft.value === EMPTY_DRAFT ? NO_ERRORS : validateDraft(draft.value)))
  const errors = computed(() => mergeErrors(clientErrors.value, serverErrors.value))
  const invalidRoomKeys = computed(() => Object.keys(errors.value.rooms))

  const rooms = computed(() => draft.value.rooms)
  const selectedRoom = computed(
    () => rooms.value.find((room) => room.key === selection.value.roomKey) ?? null,
  )

  function load(floor: FloorDto): void {
    floorId.value = floor.id
    server.value = floor
    rowVersion.value = floor.rowVersion
    const loaded = draftFromFloor(floor)
    ids = loaded.ids
    stack.reset(loaded.draft)
    selection.value = { roomKey: null, vertex: null }
    drawing.value = emptyDraw()
    calibration.value = emptyCalibration()
    serverErrors.value = null
    attemptedSave.value = false
    if (!floor.background && (tool.value === 'calibrate' || tool.value === 'background'))
      tool.value = 'select'
  }

  // --- Selection and tools ------------------------------------------------------------------------------------------
  function select(roomKey: string | null, vertex: number | null = null): void {
    selection.value = { roomKey, vertex }
    if (roomKey) panelTab.value = 'rooms'
  }

  function setTool(next: EditorTool): void {
    if (next === 'calibrate' && !draft.value.background) return
    tool.value = next
    drawing.value = emptyDraw()
    calibration.value = emptyCalibration()
    // A highlight from the previous tool would stick (Draw highlights corners and walls, never whole rooms).
    hoverRoomKey.value = null
    if (next === 'background' || next === 'calibrate') panelTab.value = 'background'
    if (next === 'draw') selection.value = { roomKey: null, vertex: null }
  }

  // --- Edits (each one undoable) ------------------------------------------------------------------------------------
  function withRooms(next: DraftRoom[]): Draft {
    return { ...draft.value, rooms: next }
  }

  function mapRoom(key: string, change: (room: DraftRoom) => DraftRoom): Draft {
    return withRooms(draft.value.rooms.map((room) => (room.key === key ? change(room) : room)))
  }

  /** Adds a drawn room (named "Room n", the least used color) and selects it. */
  function addRoom(polygon: Point[]): string {
    const key = newRoomKey()
    const room: DraftRoom = {
      key,
      name: nextRoomName(draft.value.rooms, options.roomBaseName()),
      icon: DEFAULT_ROOM_ICON,
      colorToken: nextColorToken(draft.value.rooms),
      polygon: polygon.map((p) => ({ x: p.x, y: p.y })),
    }
    stack.execute(withRooms([...draft.value.rooms, room]), 'floors.editor.history.addRoom')
    select(key)
    return key
  }

  function updateRoom(
    key: string,
    patch: Partial<Omit<DraftRoom, 'key'>>,
    label: string,
    mergeKey?: string,
  ): void {
    stack.execute(
      mapRoom(key, (room) => ({ ...room, ...patch })),
      label,
      mergeKey,
    )
  }

  /** Deletes a room (one undo step); `null` when there is no such room. */
  function deleteRoom(key: string): DeletedRoom | null {
    const room = draft.value.rooms.find((entry) => entry.key === key)
    if (!room) return null
    stack.execute(
      withRooms(draft.value.rooms.filter((entry) => entry.key !== key)),
      'floors.editor.history.deleteRoom',
    )
    if (selection.value.roomKey === key) selection.value = { roomKey: null, vertex: null }
    if (hoverRoomKey.value === key) hoverRoomKey.value = null
    const after = draft.value
    let used = false
    const deleted: DeletedRoom = {
      name: room.name,
      undo: () => {
        if (used || draft.value !== after) return false
        used = true
        stack.undo()
        select(key)
        return true
      },
    }
    options.onRoomDeleted?.(deleted)
    return deleted
  }

  function updateFloor(patch: Partial<DraftFloor>, mergeKey?: string): void {
    stack.execute(
      { ...draft.value, floor: { ...draft.value.floor, ...patch } },
      'floors.editor.history.floor',
      mergeKey,
    )
  }

  function setBackground(background: FloorBackgroundDto | null, label: string, mergeKey?: string): void {
    stack.execute({ ...draft.value, background }, label, mergeKey)
    if (!background && (tool.value === 'calibrate' || tool.value === 'background')) tool.value = 'select'
  }

  /** A room or background drag previews every move but records one undo step. */
  const preview = {
    begin: () => stack.begin(),
    room: (key: string, polygon: Point[]) => stack.update(mapRoom(key, (room) => ({ ...room, polygon }))),
    background: (background: FloorBackgroundDto) => stack.update({ ...draft.value, background }),
    commit: (label: string) => stack.commit(label),
    rollback: () => stack.rollback(),
  }

  /** While drawing, Undo takes back the last corner placed (touch screens have no Backspace). */
  const undoesCorner = computed(() => tool.value === 'draw' && drawing.value.points.length > 0)

  function undo(): void {
    if (undoesCorner.value) {
      drawing.value = removeLastDrawPoint(drawing.value)
      return
    }
    stack.undo()
    keepSelectionValid()
  }

  function redo(): void {
    stack.redo()
    keepSelectionValid()
  }

  function keepSelectionValid(): void {
    const room = selectedRoom.value
    if (!room) selection.value = { roomKey: null, vertex: null }
    else if (selection.value.vertex !== null && selection.value.vertex >= room.polygon.length) {
      selection.value = { roomKey: room.key, vertex: null }
    }
  }

  // --- Saving -------------------------------------------------------------------------------------------------------
  async function save(): Promise<SaveOutcome> {
    const id = floorId.value
    if (id === null || saving.value) return { kind: 'nothing' }
    serverErrors.value = null
    if (clientErrors.value.count > 0) {
      attemptedSave.value = true
      return { kind: 'invalid' }
    }
    const target = draft.value
    const steps = planSave(stack.saved.value, target)
    if (steps.length === 0) return { kind: 'nothing' }

    saving.value = true
    let sentKeys: string[] = []
    try {
      for (const step of steps) {
        if (step.kind === 'floor') {
          const floor = unwrap(
            await apiUpdateFloor({
              path: { id },
              body: floorRequest(target, rowVersion.value, {
                widthCm: step.widthCm,
                heightCm: step.heightCm,
              }),
            }),
          )
          rowVersion.value = floor.rowVersion
          server.value = floor
        } else {
          sentKeys = target.rooms.map((room) => room.key)
          const floor = unwrap(
            await saveFloorLayout({ path: { id }, body: layoutRequest(target, ids, rowVersion.value) }),
          )
          rowVersion.value = floor.rowVersion
          server.value = floor
          // The server answers the rooms in the order they were sent.
          ids = new Map(sentKeys.map((key, index) => [key, floor.rooms[index]!.id]))
        }
      }
      // The draft may have changed while the requests ran (edits are not blocked): only what was sent is saved.
      stack.markSaved(target)
      attemptedSave.value = false
      return { kind: 'saved' }
    } catch (error) {
      if (errorCodeOf(error) === 'floors.concurrency') return { kind: 'conflict' }
      const fields = fieldErrorsOf(error)
      if (fields) {
        serverErrors.value = errorsFromProblem(fields, sentKeys)
        attemptedSave.value = true
        return { kind: 'invalid' }
      }
      return { kind: 'failed', error }
    } finally {
      saving.value = false
    }
  }

  return {
    // state
    draft,
    floorId,
    server,
    rowVersion,
    rooms,
    tool,
    selection,
    selectedRoom,
    hoverRoomKey,
    drawing,
    calibration,
    panelTab,
    backgroundSize,
    errors,
    invalidRoomKeys,
    saving,
    attemptedSave,
    // history
    canUndo: computed(() => undoesCorner.value || stack.canUndo.value),
    canRedo: stack.canRedo,
    undoLabel: computed(() =>
      undoesCorner.value ? 'floors.editor.history.placeCorner' : stack.undoLabel.value,
    ),
    redoLabel: stack.redoLabel,
    isDirty: stack.isDirty,
    undo,
    redo,
    // actions
    load,
    select,
    setTool,
    addRoom,
    updateRoom,
    deleteRoom,
    updateFloor,
    setBackground,
    preview,
    save,
  }
}

export type EditorState = ReturnType<typeof useEditorState>

const EDITOR_KEY: InjectionKey<EditorState> = Symbol('floor-editor')

export function provideEditor(state: EditorState): void {
  provide(EDITOR_KEY, state)
}

/** The editor state provided by the editor page. */
export function useEditor(): EditorState {
  const state = inject(EDITOR_KEY)
  if (!state) throw new Error('useEditor() needs the floor editor page (provideEditor).')
  return state
}
