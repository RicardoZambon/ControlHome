import type { DeviceSummaryDto } from '@controlhome/api-client'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useDevicesStore } from '../../../stores/devices'
import { createTestPlugins } from '../../../test/app'
import { cameraDevice } from '../../../test/devices'
import { SNAPSHOT_REFRESH_MS } from '../camera/useCameraSnapshot'
import CameraControl from './CameraControl.vue'

// The camera control: the refreshing snapshot (object URLs revoked on replace and unmount), its placeholder, the swap to
// the live player, and the motion badge.

const api = vi.hoisted(() => ({ getCameraSnapshot: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

const PlayerStub = defineComponent({
  name: 'CameraPlayer',
  inheritAttrs: false,
  setup:
    (_, { attrs }) =>
    () =>
      h('div', { 'data-slot': 'player-stub', 'data-snapshot': attrs['snapshot-url'] }),
})

const picture = () => ({
  data: new Blob(['jpeg'], { type: 'image/jpeg' }),
  error: undefined,
  response: new Response(null, { status: 200 }),
})
const noSnapshot = () => ({
  data: undefined,
  error: { status: 404, code: 'cameras.no_snapshot' },
  response: new Response(null, { status: 404 }),
})

let urls = 0
const revokeObjectURL = vi.fn()
let visibility: DocumentVisibilityState = 'visible'

function mountControl(device: DeviceSummaryDto = cameraDevice()) {
  const plugins = createTestPlugins()
  useDevicesStore().seedValues([device])
  const endpoint = device.endpoints[0]!
  return mount(CameraControl, {
    props: { device, endpoint, capability: endpoint.capabilities[0]! },
    attachTo: document.body,
    global: { plugins: [plugins.pinia, plugins.i18n, plugins.router], stubs: { CameraPlayer: PlayerStub } },
  })
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  urls = 0
  visibility = 'visible'
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:snapshot-${++urls}`)
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(revokeObjectURL)
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
})

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})
enableAutoUnmount(afterEach)

describe('CameraControl', () => {
  it('shows the snapshot, refreshes it every 10 s and revokes each replaced picture', async () => {
    api.getCameraSnapshot.mockImplementation(async () => picture())
    const wrapper = mountControl()
    await flushPromises()

    expect(api.getCameraSnapshot).toHaveBeenCalledWith({ path: { id: 'e-cam' }, parseAs: 'blob' })
    const img = () => wrapper.get('[data-slot="camera-snapshot"] img')
    expect(img().attributes('src')).toBe('blob:snapshot-1')
    expect(img().attributes('alt')).toBe('Latest picture from Garden camera')

    await vi.advanceTimersByTimeAsync(SNAPSHOT_REFRESH_MS)
    await flushPromises()
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(2)
    expect(img().attributes('src')).toBe('blob:snapshot-2')
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:snapshot-1')
    expect(revokeObjectURL).not.toHaveBeenCalledWith('blob:snapshot-2')

    wrapper.unmount()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:snapshot-2')
    await vi.advanceTimersByTimeAsync(SNAPSHOT_REFRESH_MS * 2)
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(2)
  })

  it('keeps the placeholder when the camera has no picture', async () => {
    api.getCameraSnapshot.mockImplementation(async () => noSnapshot())
    const wrapper = mountControl()
    await flushPromises()

    expect(wrapper.find('[data-slot="camera-snapshot"] img').exists()).toBe(false)
    expect(wrapper.get('[data-slot="camera-snapshot"]').text()).toContain(
      'No picture from this camera right now.',
    )
    expect(wrapper.get('[data-slot="camera-snapshot"]').text()).toContain('Stream available')
  })

  it('pauses refreshing while the page is hidden and refreshes when it is visible again', async () => {
    api.getCameraSnapshot.mockImplementation(async () => picture())
    mountControl()
    await flushPromises()

    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(SNAPSHOT_REFRESH_MS * 3)
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(1)

    visibility = 'visible'
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(2)
  })

  it('swaps the snapshot for the live player and back; no refreshing while live', async () => {
    api.getCameraSnapshot.mockImplementation(async () => picture())
    const wrapper = mountControl()
    await flushPromises()

    await wrapper.get('[data-slot="camera-watch"]').trigger('click')
    const player = wrapper.get('[data-slot="player-stub"]')
    expect(player.attributes('data-snapshot')).toBe('blob:snapshot-1')
    await vi.advanceTimersByTimeAsync(SNAPSHOT_REFRESH_MS * 2)
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(1)

    await wrapper.get('[data-slot="camera-stop"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-slot="player-stub"]').exists()).toBe(false)
    expect(api.getCameraSnapshot).toHaveBeenCalledTimes(2)
  })

  it('offers live view only when the camera has a stream', async () => {
    api.getCameraSnapshot.mockImplementation(async () => picture())
    const wrapper = mountControl(cameraDevice({ stream: false }))
    await flushPromises()
    expect(wrapper.find('[data-slot="camera-watch"]').exists()).toBe(false)
    expect(wrapper.get('[data-slot="camera-snapshot"]').text()).toContain('No stream')
  })

  it('shows a motion badge while the device reports motion', async () => {
    api.getCameraSnapshot.mockImplementation(async () => picture())
    const wrapper = mountControl(cameraDevice({ motion: true }))
    await flushPromises()
    expect(wrapper.get('[data-slot="motion-badge"]').text()).toBe('Motion')
  })
})
