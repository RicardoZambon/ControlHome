import type { PlacementDto } from '@controlhome/api-client'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CATALOGUE, INTEGRATIONS, ROOMS, basicHome } from '../../../test/devices'
import { usePlacementEditor, type PlacementEditorOptions } from './usePlacementEditor'

const api = vi.hoisted(() => ({
  listPlacements: vi.fn(),
  placeOnFloor: vi.fn(),
  deletePlacement: vi.fn(),
  listDevices: vi.fn(),
  listCapabilities: vi.fn(),
  listRooms: vi.fn(),
  listIntegrations: vi.fn(),
}))

vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const ok = (data: unknown, status = 200) => ({
  data,
  error: undefined,
  response: new Response(null, { status }),
})
const fail = (code: string, status = 400) => ({
  data: undefined,
  error: { code, status },
  response: new Response(null, { status }),
})

function placement(
  id: number,
  floorId: number,
  target: { endpointId?: string; deviceId?: string },
): PlacementDto {
  return {
    id,
    floorId,
    endpointId: target.endpointId ?? null,
    deviceId: target.deviceId ?? null,
    xCm: 100,
    yCm: 100,
    roomId: null,
  }
}

function setup(overrides: Partial<PlacementEditorOptions> = {}) {
  const options: PlacementEditorOptions = {
    floorId: () => 1,
    floorSize: () => ({ widthCm: 1000, heightCm: 800 }),
    toCm: (x, y) => ({ x: x * 10, y: y * 10 }),
    planRect: () => ({ left: 0, top: 0, right: 100, bottom: 80, width: 100, height: 80 }) as DOMRect,
    onArm: vi.fn(),
    onError: vi.fn(),
    ...overrides,
  }
  return { editor: usePlacementEditor(options), options }
}

function pointer(type: string, x: number, y: number): PointerEvent {
  const target = document.createElement('button')
  const event = new PointerEvent(type, { pointerId: 3, button: 0, clientX: x, clientY: y })
  Object.defineProperty(event, 'currentTarget', { value: target })
  return event
}

beforeEach(() => {
  vi.resetAllMocks()
  setActivePinia(createPinia())
  api.listDevices.mockResolvedValue(ok(basicHome()))
  api.listCapabilities.mockResolvedValue(ok(CATALOGUE))
  api.listRooms.mockResolvedValue(ok(ROOMS))
  api.listIntegrations.mockResolvedValue(ok(INTEGRATIONS))
  api.listPlacements.mockResolvedValue(
    ok([placement(1, 1, { endpointId: 'e-light' }), placement(2, 2, { endpointId: 'e-plug' })]),
  )
})

describe('usePlacementEditor', () => {
  it('splits what is on this floor, elsewhere and on no plan', async () => {
    const { editor } = setup()
    await editor.load([placement(1, 1, { endpointId: 'e-light' })])
    expect(editor.markers.value.map((marker) => marker.name)).toEqual(['Living room light'])
    expect(editor.elsewhere.value).toBe(1)
    expect(editor.groups.value.map((group) => group.device.id)).toEqual(['d-ac', 'd-contact', 'd-lock'])
  })

  it('places a device at a point of the floor and selects it', async () => {
    const { editor } = setup()
    await editor.load([])
    api.placeOnFloor.mockResolvedValue(
      ok({ ...placement(7, 1, { endpointId: 'e-ac' }), xCm: 250, yCm: 120, roomId: 12 }),
    )
    expect(await editor.place({ endpointId: 'e-ac' }, { x: 250.4, y: 119.6 })).toBe(true)
    expect(api.placeOnFloor).toHaveBeenCalledWith({
      body: { endpointId: 'e-ac', deviceId: null, floorId: 1, xCm: 250, yCm: 120 },
    })
    expect(editor.selectedId.value).toBe(7)
    expect(editor.groups.value.map((group) => group.device.id)).not.toContain('d-ac')
  })

  it('ignores a point outside the floor', async () => {
    const { editor } = setup()
    await editor.load([])
    expect(await editor.place({ endpointId: 'e-ac' }, { x: 1200, y: 10 })).toBe(false)
    expect(api.placeOnFloor).not.toHaveBeenCalled()
  })

  it('moves optimistically and puts the device back when the save fails', async () => {
    const { editor, options } = setup()
    await editor.load([])
    api.placeOnFloor.mockResolvedValue(fail('validation.failed'))
    await editor.move(1, { x: 600, y: 500 })
    expect(editor.all.value.find((entry) => entry.id === 1)?.xCm).toBe(100)
    expect(options.onError).toHaveBeenCalled()

    api.placeOnFloor.mockResolvedValue(
      ok({ ...placement(1, 1, { endpointId: 'e-light' }), xCm: 600, yCm: 500, roomId: 11 }),
    )
    await editor.move(1, { x: 600, y: 500 })
    expect(editor.all.value.find((entry) => entry.id === 1)).toMatchObject({ xCm: 600, roomId: 11 })
  })

  it('takes a device off the plan', async () => {
    const { editor } = setup()
    await editor.load([])
    editor.select(1)
    api.deletePlacement.mockResolvedValue(ok(undefined, 204))
    expect(await editor.remove(1)).toBe(true)
    expect(editor.selectedId.value).toBeNull()
    expect(editor.markers.value).toEqual([])
    expect(editor.groups.value.map((group) => group.device.id)).toContain('d-light')
  })

  it('arms a device for tap-to-place (the page closes its sheet)', async () => {
    const { editor, options } = setup()
    await editor.load([])
    editor.select(1)
    editor.arm({ endpointId: 'e-ac' }, 'Bedroom air conditioner')
    expect(editor.armed.value?.name).toBe('Bedroom air conditioner')
    expect(editor.selectedId.value).toBeNull()
    expect(options.onArm).toHaveBeenCalled()
    expect(editor.isBusyWith({ endpointId: 'e-ac' })).toBeTruthy()
    editor.disarm()
    expect(editor.armed.value).toBeNull()
  })

  it('drops a dragged device where it is let go over the plan', async () => {
    const { editor } = setup()
    await editor.load([])
    api.placeOnFloor.mockResolvedValue(ok(placement(9, 1, { endpointId: 'e-lock' })))
    editor.startDrag({ endpointId: 'e-lock' }, 'Front door lock', pointer('pointerdown', 200, 200))
    expect(editor.drag.value?.overPlan).toBe(false)
    editor.moveDrag(pointer('pointermove', 40, 30))
    expect(editor.drag.value?.overPlan).toBe(true)
    await editor.endDrag(pointer('pointerup', 40, 30))
    await flushPromises()
    expect(editor.drag.value).toBeNull()
    expect(api.placeOnFloor).toHaveBeenCalledWith({
      body: { endpointId: 'e-lock', deviceId: null, floorId: 1, xCm: 400, yCm: 300 },
    })
  })

  it('drops nothing when let go outside the plan', async () => {
    const { editor } = setup()
    await editor.load([])
    editor.startDrag({ endpointId: 'e-lock' }, 'Front door lock', pointer('pointerdown', 40, 30))
    await editor.endDrag(pointer('pointerup', 300, 300))
    expect(api.placeOnFloor).not.toHaveBeenCalled()
  })
})
