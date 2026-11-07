import type { DeviceSummaryDto } from '@controlhome/api-client'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useDevicesStore } from '../../../stores/devices'
import { createTestPlugins } from '../../../test/app'
import { cameraDevice } from '../../../test/devices'
import DeviceTile from '../DeviceTile.vue'

// A camera's tile: the snapshot thumbnail, the motion badge, and live view in a dialog.

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
      h('div', { 'data-slot': 'player-stub', 'data-endpoint': attrs['endpoint-id'] }),
})

function mountTile(device: DeviceSummaryDto) {
  const plugins = createTestPlugins()
  useDevicesStore().seedValues([device])
  return mount(DeviceTile, {
    props: { device, endpoint: device.endpoints[0]! },
    attachTo: document.body,
    global: { plugins: [plugins.pinia, plugins.i18n, plugins.router], stubs: { CameraPlayer: PlayerStub } },
  })
}

beforeEach(() => {
  vi.resetAllMocks()
  api.getCameraSnapshot.mockResolvedValue({
    data: new Blob(['jpeg'], { type: 'image/jpeg' }),
    error: undefined,
    response: new Response(null, { status: 200 }),
  })
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:tile')
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
})

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})
enableAutoUnmount(afterEach)

describe('camera tile', () => {
  it('shows the snapshot and opens live view in a dialog', async () => {
    const wrapper = mountTile(cameraDevice())
    await flushPromises()

    const watch = wrapper.get('[data-slot="tile-camera-watch"]')
    expect(watch.get('img').attributes('src')).toBe('blob:tile')
    expect(watch.text()).toBe('Watch Garden camera live')
    expect(wrapper.find('[data-slot="motion-badge"]').exists()).toBe(false)

    await watch.trigger('click')
    await flushPromises()
    const dialog = document.querySelector('[role="dialog"]')
    expect(dialog?.textContent).toContain('Garden camera')
    expect(dialog?.querySelector('[data-slot="player-stub"]')?.getAttribute('data-endpoint')).toBe('e-cam')
  })

  it('shows the motion badge while the camera reports motion', async () => {
    const wrapper = mountTile(cameraDevice({ motion: true }))
    await flushPromises()
    expect(wrapper.get('[data-slot="tile-camera"] [data-slot="motion-badge"]').text()).toBe('Motion')
  })

  it('shows a plain picture (no live view) when the camera has no stream', async () => {
    const wrapper = mountTile(cameraDevice({ stream: false }))
    await flushPromises()
    expect(wrapper.find('[data-slot="tile-camera-watch"]').exists()).toBe(false)
    expect(wrapper.get('[data-slot="tile-camera"] img').attributes('alt')).toBe(
      'Latest picture from Garden camera',
    )
  })
})
