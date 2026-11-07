import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { createAppI18n } from '../../../i18n'
import CameraPlayer from './CameraPlayer.vue'
import { liveTimings } from './useCameraLive'

// The live player's state machine against fake RTCPeerConnection, WebSocket and MediaSource (jsdom has none of them).

const api = vi.hoisted(() => ({ exchangeCameraWebRtc: vi.fn() }))
vi.mock('@controlhome/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@controlhome/api-client')>()),
  ...api,
}))

class FakePeerConnection extends EventTarget {
  static instances: FakePeerConnection[] = []
  static failOffer = false
  iceGatheringState = 'complete'
  connectionState = 'new'
  localDescription: { type: string; sdp: string } | null = null
  remoteDescription: { type: string; sdp: string } | null = null
  transceivers: { kind: string; direction: string }[] = []
  onconnectionstatechange: (() => void) | null = null
  close = vi.fn()

  constructor() {
    super()
    FakePeerConnection.instances.push(this)
  }
  addTransceiver(kind: string, init: { direction: string }) {
    this.transceivers.push({ kind, direction: init.direction })
    return { receiver: { track: { kind } } }
  }
  async createOffer() {
    if (FakePeerConnection.failOffer) throw new Error('no codecs')
    return { type: 'offer', sdp: 'v=0 offer a=recvonly' }
  }
  async setLocalDescription(description: { type: string; sdp: string }) {
    this.localDescription = description
  }
  async setRemoteDescription(description: { type: string; sdp: string }) {
    this.remoteDescription = description
  }
}

class FakeMediaStream {
  constructor(readonly tracks: unknown[]) {}
}

class FakeSourceBuffer extends EventTarget {
  updating = false
  mode = 'sequence'
  buffered = { length: 0, start: () => 0, end: () => 0 }
  appended: ArrayBuffer[] = []
  appendBuffer(data: ArrayBuffer) {
    this.appended.push(data)
  }
  remove = vi.fn()
}

class FakeMediaSource extends EventTarget {
  static instances: FakeMediaSource[] = []
  static isTypeSupported = (type: string) => type.includes('avc1.640029') || type.includes('mp4a.40.2')
  buffers: { mime: string; buffer: FakeSourceBuffer }[] = []
  constructor() {
    super()
    FakeMediaSource.instances.push(this)
  }
  addSourceBuffer(mime: string) {
    const buffer = new FakeSourceBuffer()
    this.buffers.push({ mime, buffer })
    return buffer
  }
}

class FakeWebSocket {
  static instances: FakeWebSocket[] = []
  binaryType = 'blob'
  sent: string[] = []
  onopen: (() => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: (() => void) | null = null
  onerror: (() => void) | null = null
  close = vi.fn()
  constructor(readonly url: string) {
    FakeWebSocket.instances.push(this)
  }
  send(data: string) {
    this.sent.push(data)
  }
}

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const problem = (status: number, code: string) => ({
  data: undefined,
  error: { status, code },
  response: new Response(null, { status }),
})

let objectUrls = 0
const createObjectURL = vi.fn(() => `blob:media-${++objectUrls}`)
const revokeObjectURL = vi.fn()
let visibility: DocumentVisibilityState = 'visible'

function mountPlayer(props: Record<string, unknown> = {}) {
  return mount(CameraPlayer, {
    props: { endpointId: 'e-cam', name: 'Garden camera', snapshotUrl: 'blob:snapshot', ...props },
    attachTo: document.body,
    global: { plugins: [createAppI18n({ locale: 'en' })] },
  })
}

function frame(wrapper: ReturnType<typeof mountPlayer>): void {
  wrapper.get('video').element.dispatchEvent(new Event('loadeddata'))
}

function setVisibility(state: DocumentVisibilityState): void {
  visibility = state
  document.dispatchEvent(new Event('visibilitychange'))
}

/** Lets the fallback to MSE run: the MediaSource opens, then the socket connects. */
async function openMse(): Promise<FakeWebSocket> {
  await flushPromises()
  const source = FakeMediaSource.instances.at(-1)!
  source.dispatchEvent(new Event('sourceopen'))
  const socket = FakeWebSocket.instances.at(-1)!
  socket.onopen?.()
  return socket
}

beforeEach(() => {
  vi.resetAllMocks()
  FakePeerConnection.instances = []
  FakePeerConnection.failOffer = false
  FakeMediaSource.instances = []
  FakeWebSocket.instances = []
  objectUrls = 0
  visibility = 'visible'
  Object.assign(liveTimings, { iceGathering: 50, webrtcFirstFrame: 5_000, mseFirstFrame: 10_000 })
  vi.stubGlobal('RTCPeerConnection', FakePeerConnection)
  vi.stubGlobal('MediaStream', FakeMediaStream)
  vi.stubGlobal('MediaSource', FakeMediaSource)
  vi.stubGlobal('WebSocket', FakeWebSocket)
  vi.spyOn(URL, 'createObjectURL').mockImplementation(createObjectURL)
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(revokeObjectURL)
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined)
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})
// Registered after the cleanup above, so (hooks run as a stack) players unmount before the fakes are removed.
enableAutoUnmount(afterEach)

describe('CameraPlayer', () => {
  it('plays over WebRTC: a receive-only offer, the answer, then live at the first frame', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(ok({ sdp: 'v=0 answer' }))
    const wrapper = mountPlayer()
    await nextTick()
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('connecting')
    expect(wrapper.get('[role="status"]').text()).toBe('Connecting to live video…')
    expect(wrapper.find('[data-slot="camera-backdrop"]').exists()).toBe(true)

    await flushPromises()
    const pc = FakePeerConnection.instances[0]!
    expect(pc.transceivers).toEqual([
      { kind: 'video', direction: 'recvonly' },
      { kind: 'audio', direction: 'recvonly' },
    ])
    expect(api.exchangeCameraWebRtc).toHaveBeenCalledWith({
      path: { id: 'e-cam' },
      body: { sdp: 'v=0 offer a=recvonly', quality: 'main' },
    })
    expect(pc.remoteDescription).toEqual({ type: 'answer', sdp: 'v=0 answer' })

    frame(wrapper)
    await flushPromises()
    const player = wrapper.get('[data-slot="camera-player"]')
    expect(player.attributes('data-status')).toBe('live')
    expect(player.attributes('data-transport')).toBe('webrtc')
    expect(wrapper.get('[role="status"]').attributes('aria-live')).toBe('polite')
    expect(wrapper.get('[data-slot="camera-live"]').text()).toContain('Live')
    expect(wrapper.get('[data-slot="camera-live"]').text()).toContain('WebRTC')
    expect(wrapper.find('[data-slot="camera-backdrop"]').exists()).toBe(false)
    expect(FakeWebSocket.instances).toHaveLength(0)

    const video = wrapper.get('video')
    expect(video.attributes('aria-label')).toBe('Live video from Garden camera')
    expect((video.element as HTMLVideoElement).muted).toBe(true)
    expect(video.attributes()).toHaveProperty('playsinline')
    expect(video.attributes()).toHaveProperty('autoplay')
  })

  it('falls back to MSE over the WebSocket when the WebRTC exchange fails', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(problem(400, 'validation.failed'))
    const wrapper = mountPlayer()
    const socket = await openMse()

    expect(FakePeerConnection.instances[0]!.close).toHaveBeenCalled()
    expect(socket.url).toBe('ws://localhost:3000/api/endpoints/e-cam/camera/stream?quality=main')
    expect(socket.binaryType).toBe('arraybuffer')
    expect(JSON.parse(socket.sent[0]!)).toEqual({ type: 'mse', value: 'avc1.640029,mp4a.40.2' })

    socket.onmessage?.({ data: JSON.stringify({ type: 'mse', value: 'video/mp4; codecs="avc1.640029"' }) })
    const source = FakeMediaSource.instances[0]!
    expect(source.buffers[0]!.mime).toBe('video/mp4; codecs="avc1.640029"')
    const segment = new ArrayBuffer(8)
    socket.onmessage?.({ data: segment })
    expect(source.buffers[0]!.buffer.appended).toEqual([segment])

    frame(wrapper)
    await flushPromises()
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-transport')).toBe('mse')
    expect(wrapper.get('[role="status"]').text()).toContain('MSE')
  })

  it('falls back to MSE when WebRTC shows no frame in time', async () => {
    liveTimings.webrtcFirstFrame = 20
    api.exchangeCameraWebRtc.mockResolvedValue(ok({ sdp: 'v=0 answer' }))
    const wrapper = mountPlayer()
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 40))
    const socket = await openMse()
    expect(socket.sent).toHaveLength(1)
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('connecting')
  })

  it('falls back to MSE when the browser cannot make an offer', async () => {
    FakePeerConnection.failOffer = true
    mountPlayer()
    await openMse()
    expect(api.exchangeCameraWebRtc).not.toHaveBeenCalled()
    expect(FakeWebSocket.instances).toHaveLength(1)
  })

  it('shows why live video is unavailable (503 live_disabled) over the last snapshot, without MSE', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(problem(503, 'cameras.live_disabled'))
    const wrapper = mountPlayer()
    await flushPromises()

    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('unavailable')
    expect(wrapper.get('[data-slot="camera-message"]').text()).toBe(
      "Live video isn't set up on this server. An administrator can turn it on (go2rtc).",
    )
    expect(wrapper.get('[data-slot="camera-backdrop"]').attributes('src')).toBe('blob:snapshot')
    expect(FakeMediaSource.instances).toHaveLength(0)
    expect(FakeWebSocket.instances).toHaveLength(0)

    // Try again starts over.
    api.exchangeCameraWebRtc.mockResolvedValue(ok({ sdp: 'v=0 answer' }))
    await wrapper.get('[data-slot="camera-retry"]').trigger('click')
    await flushPromises()
    expect(FakePeerConnection.instances).toHaveLength(2)
  })

  it('shows an error with retry when MSE closes before playing', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(problem(400, 'validation.failed'))
    const wrapper = mountPlayer()
    const socket = await openMse()
    socket.onclose?.()
    await flushPromises()
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('error')
    expect(wrapper.get('[data-slot="camera-retry"]').text()).toBe('Try again')
    expect(socket.close).toHaveBeenCalled()
  })

  it('switches quality: reconnects with the sub stream', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(ok({ sdp: 'v=0 answer' }))
    const wrapper = mountPlayer()
    await flushPromises()
    await wrapper.get('[data-slot="camera-quality"] button[value="sub"]').trigger('click')
    await flushPromises()
    expect(FakePeerConnection.instances[0]!.close).toHaveBeenCalled()
    expect(api.exchangeCameraWebRtc).toHaveBeenLastCalledWith({
      path: { id: 'e-cam' },
      body: { sdp: 'v=0 offer a=recvonly', quality: 'sub' },
    })
  })

  it('closes the peer connection, socket, MediaSource and object URLs on unmount', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(problem(400, 'validation.failed'))
    const wrapper = mountPlayer()
    const socket = await openMse()
    const pc = FakePeerConnection.instances[0]!

    wrapper.unmount()

    expect(pc.close).toHaveBeenCalled()
    expect(socket.close).toHaveBeenCalled()
    expect(socket.onmessage).toBeNull()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:media-1')
    expect(HTMLMediaElement.prototype.load).toHaveBeenCalled()
  })

  it('stops while the page is hidden and reconnects when it is visible again', async () => {
    api.exchangeCameraWebRtc.mockResolvedValue(ok({ sdp: 'v=0 answer' }))
    const wrapper = mountPlayer()
    await flushPromises()
    frame(wrapper)
    await flushPromises()
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('live')

    setVisibility('hidden')
    await flushPromises()
    expect(FakePeerConnection.instances[0]!.close).toHaveBeenCalled()
    expect(wrapper.get('[data-slot="camera-player"]').attributes('data-status')).toBe('idle')

    setVisibility('visible')
    await flushPromises()
    expect(FakePeerConnection.instances).toHaveLength(2)
    expect(api.exchangeCameraWebRtc).toHaveBeenCalledTimes(2)
  })

  it('shows a motion badge while the device reports motion', () => {
    api.exchangeCameraWebRtc.mockReturnValue(new Promise(() => {}))
    const wrapper = mountPlayer({ motion: true })
    expect(wrapper.get('[data-slot="motion-badge"]').text()).toBe('Motion')
  })
})
