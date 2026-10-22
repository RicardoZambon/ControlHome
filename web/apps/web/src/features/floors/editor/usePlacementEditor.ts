import { deletePlacement, listPlacements, placeOnFloor, type PlacementDto } from '@controlhome/api-client'
import { computed, inject, provide, ref, shallowRef, type InjectionKey } from 'vue'
import { isWithinFloor, type Point } from '../../../components/floorplan/geometry'
import {
  planMarkers,
  targetKey,
  targetOf,
  unplacedGroups,
  upsertPlacement,
  type PlacementTarget,
} from '../../../components/floorplan/placements'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'

/** A device being dragged from the list onto the plan. */
export interface PlanDrag {
  target: PlacementTarget
  name: string
  pointerId: number
  /** The pointer, in client px. */
  x: number
  y: number
  /** The pointer is over the plan (dropping places it there). */
  overPlan: boolean
}

export interface PlacementEditorOptions {
  floorId: () => number | null
  floorSize: () => { widthCm: number; heightCm: number }
  /** Client px → floor cm (the canvas). */
  toCm: (clientX: number, clientY: number) => Point | undefined
  /** The plan's box on screen, to know whether a drag ends over it. */
  planRect: () => DOMRect | undefined
  /** A device is armed for tap-to-place (phones close the panel's sheet so the plan can be tapped). */
  onArm?: () => void
  /** Saving failed: the page shows the problem. */
  onError: (error: unknown) => void
}

/**
 * The floor editor's "Devices" tab: the devices on this floor (moved by dragging their markers, taken off the plan),
 * what is on no plan yet (dragged onto the plan, or armed with "Place" and then tapped on the plan) and the selection.
 * Placements are saved at once (`PUT /api/placements`, which also puts the device in the room under its point); they
 * are not part of the layout draft or its undo history.
 */
export function usePlacementEditor(options: PlacementEditorOptions) {
  const devices = useDevicesStore()
  /** Every placement of every floor (what is "on a plan"). */
  const all = shallowRef<PlacementDto[]>([])
  const loaded = ref(false)
  const busy = ref(false)
  const selectedId = ref<number | null>(null)
  const armed = shallowRef<{ target: PlacementTarget; name: string } | null>(null)
  const drag = shallowRef<PlanDrag | null>(null)
  const search = ref('')

  /** Starts from the floor's own placements (from `GET /api/floors/{id}`), then loads every floor's. */
  async function load(floorPlacements: readonly PlacementDto[]): Promise<void> {
    all.value = [...floorPlacements]
    selectedId.value = null
    armed.value = null
    try {
      const [list] = await Promise.all([listPlacements().then(unwrap), devices.load()])
      all.value = list
      loaded.value = true
    } catch (error) {
      options.onError(error)
    }
  }

  const onFloor = computed(() => all.value.filter((placement) => placement.floorId === options.floorId()))
  const markers = computed(() => planMarkers(onFloor.value, devices.devices))
  const groups = computed(() => unplacedGroups(devices.devices, all.value))
  const elsewhere = computed(() => all.value.length - onFloor.value.length)
  const selected = computed(
    () => markers.value.find((marker) => marker.placement.id === selectedId.value) ?? null,
  )

  async function save(target: PlacementTarget, point: Point): Promise<PlacementDto | undefined> {
    const floorId = options.floorId()
    if (floorId === null) return undefined
    busy.value = true
    try {
      const saved = unwrap(
        await placeOnFloor({
          body: {
            endpointId: target.endpointId ?? null,
            deviceId: target.deviceId ?? null,
            floorId,
            xCm: Math.round(point.x),
            yCm: Math.round(point.y),
          },
        }),
      )
      all.value = upsertPlacement(all.value, saved)
      return saved
    } catch (error) {
      options.onError(error)
      return undefined
    } finally {
      busy.value = false
    }
  }

  /** Places a target at a point of this floor (a point outside the floor is ignored); selects it. */
  async function place(target: PlacementTarget, point: Point): Promise<boolean> {
    const { widthCm, heightCm } = options.floorSize()
    if (!isWithinFloor(point, widthCm, heightCm)) return false
    armed.value = null
    const saved = await save(target, point)
    if (saved) selectedId.value = saved.id
    return saved !== undefined
  }

  /** Moves a placed device (optimistically: it stays where it was dropped unless the save fails). */
  async function move(placementId: number, point: Point): Promise<void> {
    const current = all.value.find((placement) => placement.id === placementId)
    const target = current && targetOf(current)
    if (!current || !target) return
    selectedId.value = placementId
    const before = all.value
    all.value = upsertPlacement(all.value, { ...current, xCm: point.x, yCm: point.y })
    const saved = await save(target, point)
    if (!saved) all.value = before
  }

  /** Takes a device off the plan (it keeps its room). */
  async function remove(placementId: number): Promise<boolean> {
    busy.value = true
    try {
      unwrap(await deletePlacement({ path: { id: placementId } }))
      all.value = all.value.filter((placement) => placement.id !== placementId)
      if (selectedId.value === placementId) selectedId.value = null
      return true
    } catch (error) {
      options.onError(error)
      return false
    } finally {
      busy.value = false
    }
  }

  function select(placementId: number | null): void {
    selectedId.value = placementId
    if (placementId !== null) armed.value = null
  }

  /** "Place": the next tap on the plan puts the device there. */
  function arm(target: PlacementTarget, name: string): void {
    armed.value = { target, name }
    selectedId.value = null
    options.onArm?.()
  }

  function disarm(): void {
    armed.value = null
  }

  // --- Dragging from the list ------------------------------------------------------------------------------------------
  function overPlan(x: number, y: number): boolean {
    const rect = options.planRect()
    return !!rect && x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom
  }

  function startDrag(target: PlacementTarget, name: string, event: PointerEvent): void {
    if (event.button !== 0) return
    event.preventDefault()
    ;(event.currentTarget as Element | null)?.setPointerCapture?.(event.pointerId)
    armed.value = null
    drag.value = {
      target,
      name,
      pointerId: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      overPlan: overPlan(event.clientX, event.clientY),
    }
  }

  function moveDrag(event: PointerEvent): void {
    const current = drag.value
    if (!current || current.pointerId !== event.pointerId) return
    drag.value = {
      ...current,
      x: event.clientX,
      y: event.clientY,
      overPlan: overPlan(event.clientX, event.clientY),
    }
  }

  async function endDrag(event: PointerEvent): Promise<void> {
    const current = drag.value
    if (!current || current.pointerId !== event.pointerId) return
    drag.value = null
    if (!overPlan(event.clientX, event.clientY)) return
    const point = options.toCm(event.clientX, event.clientY)
    if (point) await place(current.target, point)
  }

  function cancelDrag(): void {
    drag.value = null
  }

  return {
    all,
    loaded,
    busy,
    selectedId,
    selected,
    armed,
    drag,
    search,
    onFloor,
    markers,
    groups,
    elsewhere,
    load,
    place,
    move,
    remove,
    select,
    arm,
    disarm,
    startDrag,
    moveDrag,
    endDrag,
    cancelDrag,
    /** Whether a target is the one armed or dragged. */
    isBusyWith: (target: PlacementTarget) =>
      (armed.value && targetKey(armed.value.target) === targetKey(target)) ||
      (drag.value && targetKey(drag.value.target) === targetKey(target)),
  }
}

export type PlacementEditor = ReturnType<typeof usePlacementEditor>

const PLACEMENT_EDITOR_KEY: InjectionKey<PlacementEditor> = Symbol('placement-editor')

export function providePlacementEditor(editor: PlacementEditor): void {
  provide(PLACEMENT_EDITOR_KEY, editor)
}

/** The placement editor provided by the floor editor page. */
export function usePlacementEditorContext(): PlacementEditor {
  const editor = inject(PLACEMENT_EDITOR_KEY)
  if (!editor) throw new Error('usePlacementEditorContext() needs the floor editor page.')
  return editor
}
