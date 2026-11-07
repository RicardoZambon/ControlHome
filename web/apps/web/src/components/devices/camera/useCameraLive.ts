import {
  client,
  exchangeCameraWebRtc,
  getProblemCode,
  type OpenCameraStreamData,
} from '@controlhome/api-client'
import { ref, toValue, type MaybeRefOrGetter, type Ref } from 'vue'

/**
 * A camera's live view, driven on a `<video>` element.
 *
 * WebRTC first: a receive-only offer goes to the app, which trades it with go2rtc for an answer; media then flows
 * straight from go2rtc. When that fails, or no frame arrives in time, it falls back to MSE: fMP4 segments over the
 * app's WebSocket (go2rtc's video-rtc.js protocol), appended to a MediaSource (ManagedMediaSource on iOS).
 *
 * States: `idle` (stopped), `connecting`, `live` (with the transport), `unavailable` (the server said why: a problem
 * code such as `cameras.live_disabled`) and `error` (something broke; retry).
 */

export type LiveStatus = 'idle' | 'connecting' | 'live' | 'unavailable' | 'error'
export type LiveTransport = 'webrtc' | 'mse'
export type LiveQuality = 'main' | 'sub'

/** Timeouts in milliseconds (tests shorten them). */
export const liveTimings = {
  /** How long to wait for the browser to gather its ICE candidates before sending the offer anyway. */
  iceGathering: 1_500,
  /** How long WebRTC may take to show a first frame before MSE takes over. */
  webrtcFirstFrame: 5_000,
  /** How long MSE may take to show a first frame before giving up. */
  mseFirstFrame: 10_000,
}

/** Problem codes that mean live video can't start now, whatever the transport: shown, no fallback. */
const UNAVAILABLE_CODES = new Set([
  'cameras.live_disabled',
  'cameras.live_unavailable',
  'cameras.live_busy',
  'cameras.no_stream',
  'commands.unavailable',
  'endpoints.not_found',
  'request.rate_limited',
])

/** The codecs go2rtc's video-rtc.js offers over MSE, in its order. */
const MSE_CODECS = [
  'avc1.640029',
  'avc1.64002A',
  'avc1.640033',
  'hvc1.1.6.L153.B0',
  'mp4a.40.2',
  'mp4a.40.5',
  'flac',
  'opus',
]

/** Seconds of video kept behind the playhead; older media is removed from the buffer. */
const KEEP_SECONDS = 10
/** The playhead jumps to the live edge when it falls this far behind. */
const MAX_LAG_SECONDS = 3

type MediaSourceClass = typeof MediaSource

function mediaSourceClass(): MediaSourceClass | undefined {
  const scope = globalThis as { ManagedMediaSource?: MediaSourceClass; MediaSource?: MediaSourceClass }
  return scope.ManagedMediaSource ?? scope.MediaSource
}

/** The same-origin WebSocket URL of the stream endpoint (the session cookie authenticates it). */
export function cameraStreamUrl(endpointId: string, quality: LiveQuality): string {
  const path = client.buildUrl<OpenCameraStreamData>({
    url: '/api/endpoints/{id}/camera/stream',
    path: { id: endpointId },
    query: { quality },
  })
  const url = new URL(path, window.location.href)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.toString()
}

function startPlayback(video: HTMLVideoElement): void {
  // Muted autoplay is allowed everywhere; a refusal (or jsdom) is not an error: the frames still arrive.
  Promise.resolve()
    .then(() => video.play())
    .catch(() => undefined)
}

export function useCameraLive(options: {
  endpointId: MaybeRefOrGetter<string>
  quality: MaybeRefOrGetter<LiveQuality>
  video: Ref<HTMLVideoElement | null | undefined>
}) {
  const status = ref<LiveStatus>('idle')
  const transport = ref<LiveTransport>()
  const problemCode = ref<string>()

  /** Bumped on every start and stop: callbacks of an older attempt see they are stale and do nothing. */
  let attempt = 0
  let cleanups: (() => void)[] = []

  function teardown(): void {
    const pending = cleanups
    cleanups = []
    for (const cleanup of pending.reverse()) {
      try {
        cleanup()
      } catch {
        // Closing something already closed: nothing to do.
      }
    }
  }

  /** Stops everything (peer connection, socket, MediaSource, object URL) and goes idle. */
  function stop(): void {
    attempt++
    teardown()
    status.value = 'idle'
    transport.value = undefined
  }

  function fail(current: number): void {
    if (current !== attempt) return
    attempt++
    teardown()
    transport.value = undefined
    status.value = 'error'
  }

  function unavailable(current: number, code: string): void {
    if (current !== attempt) return
    attempt++
    teardown()
    transport.value = undefined
    problemCode.value = code
    status.value = 'unavailable'
  }

  function goLive(current: number, kind: LiveTransport): void {
    if (current !== attempt) return
    transport.value = kind
    status.value = 'live'
  }

  /** Resolves true at the first frame, false after `timeoutMs` (listeners removed either way). */
  function firstFrame(video: HTMLVideoElement, timeoutMs: number): Promise<boolean> {
    if (video.readyState >= 2) return Promise.resolve(true)
    return new Promise((resolve) => {
      const done = (value: boolean) => {
        clearTimeout(timer)
        video.removeEventListener('loadeddata', onFrame)
        video.removeEventListener('playing', onFrame)
        resolve(value)
      }
      const onFrame = () => done(true)
      const timer = setTimeout(() => done(false), timeoutMs)
      video.addEventListener('loadeddata', onFrame)
      video.addEventListener('playing', onFrame)
      cleanups.push(() => done(false))
    })
  }

  function iceGathered(pc: RTCPeerConnection): Promise<void> {
    if (pc.iceGatheringState === 'complete') return Promise.resolve()
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer)
        pc.removeEventListener('icegatheringstatechange', check)
        resolve()
      }
      const check = () => {
        if (pc.iceGatheringState === 'complete') done()
      }
      const timer = setTimeout(done, liveTimings.iceGathering)
      pc.addEventListener('icegatheringstatechange', check)
    })
  }

  /** `live`, `fallback` (try MSE) or the problem code that makes live video unavailable. */
  async function tryWebRtc(
    current: number,
    video: HTMLVideoElement,
  ): Promise<'live' | 'fallback' | { code: string }> {
    let pc: RTCPeerConnection
    try {
      // No STUN/TURN servers: the camera restreamer is on the LAN, and nothing should leave the home network.
      pc = new RTCPeerConnection({ iceServers: [] })
    } catch {
      return 'fallback'
    }
    cleanups.push(() => {
      pc.onconnectionstatechange = null
      pc.close()
      video.srcObject = null
    })

    try {
      const video0 = pc.addTransceiver('video', { direction: 'recvonly' })
      const audio0 = pc.addTransceiver('audio', { direction: 'recvonly' })
      if (typeof MediaStream === 'function')
        video.srcObject = new MediaStream([video0.receiver.track, audio0.receiver.track])

      await pc.setLocalDescription(await pc.createOffer())
      await iceGathered(pc)
      if (current !== attempt) return 'fallback'

      const sdp = pc.localDescription?.sdp
      if (!sdp) return 'fallback'
      const result = await exchangeCameraWebRtc({
        path: { id: toValue(options.endpointId) },
        body: { sdp, quality: toValue(options.quality) },
      })
      if (current !== attempt) return 'fallback'
      if (result.error !== undefined) {
        const code = getProblemCode(result.error)
        return code && UNAVAILABLE_CODES.has(code) ? { code } : 'fallback'
      }
      await pc.setRemoteDescription({ type: 'answer', sdp: result.data.sdp })
    } catch {
      return 'fallback'
    }

    startPlayback(video)
    if (!(await firstFrame(video, liveTimings.webrtcFirstFrame))) return 'fallback'
    if (current !== attempt) return 'fallback'
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed') fail(current)
    }
    return 'live'
  }

  function tryMse(current: number, video: HTMLVideoElement): void {
    const Source = mediaSourceClass()
    if (!Source) {
      fail(current)
      return
    }
    const source = new Source()
    const objectUrl = URL.createObjectURL(source)
    if (Source !== (globalThis as { MediaSource?: MediaSourceClass }).MediaSource)
      // ManagedMediaSource plays only with remote playback (AirPlay) turned off.
      video.disableRemotePlayback = true
    video.src = objectUrl
    cleanups.push(() => {
      URL.revokeObjectURL(objectUrl)
      video.removeAttribute('src')
      video.load()
    })

    let buffer: SourceBuffer | undefined
    const queue: ArrayBuffer[] = []

    const pump = () => {
      if (!buffer || buffer.updating || current !== attempt) return
      const ranges = buffer.buffered
      if (ranges.length > 0) {
        const start = ranges.start(0)
        const end = ranges.end(ranges.length - 1)
        if (video.currentTime < end - MAX_LAG_SECONDS) video.currentTime = end - 0.5
        if (video.currentTime - start > KEEP_SECONDS * 2) {
          // Drop old media; `updateend` pumps again.
          buffer.remove(start, video.currentTime - KEEP_SECONDS)
          return
        }
      }
      const next = queue.shift()
      if (!next) return
      try {
        buffer.appendBuffer(next)
      } catch {
        fail(current)
      }
    }

    source.addEventListener(
      'sourceopen',
      () => {
        if (current !== attempt) return
        URL.revokeObjectURL(objectUrl)
        const socket = new WebSocket(cameraStreamUrl(toValue(options.endpointId), toValue(options.quality)))
        socket.binaryType = 'arraybuffer'
        cleanups.push(() => {
          socket.onopen = null
          socket.onmessage = null
          socket.onclose = null
          socket.onerror = null
          socket.close()
        })
        socket.onopen = () => {
          const codecs = MSE_CODECS.filter((codec) => Source.isTypeSupported(`video/mp4; codecs="${codec}"`))
          socket.send(JSON.stringify({ type: 'mse', value: codecs.join(',') }))
        }
        socket.onmessage = (event: MessageEvent) => {
          if (typeof event.data === 'string') {
            let message: { type?: unknown; value?: unknown }
            try {
              message = JSON.parse(event.data) as typeof message
            } catch {
              return
            }
            if (message.type === 'mse' && typeof message.value === 'string' && !buffer) {
              try {
                buffer = source.addSourceBuffer(message.value)
                buffer.mode = 'segments'
                buffer.addEventListener('updateend', pump)
              } catch {
                fail(current)
              }
            } else if (message.type === 'error') {
              fail(current)
            }
            return
          }
          if (event.data instanceof ArrayBuffer) {
            queue.push(event.data)
            pump()
          }
        }
        socket.onclose = () => fail(current)

        startPlayback(video)
        void firstFrame(video, liveTimings.mseFirstFrame).then((shown) => {
          if (shown) goLive(current, 'mse')
          else fail(current)
        })
      },
      { once: true },
    )
  }

  /** (Re)starts live view: WebRTC, then MSE. */
  async function start(): Promise<void> {
    stop()
    const current = attempt
    const video = options.video.value
    if (!video) return
    status.value = 'connecting'
    problemCode.value = undefined

    if (typeof RTCPeerConnection === 'function') {
      const outcome = await tryWebRtc(current, video)
      if (current !== attempt) return
      if (outcome === 'live') {
        goLive(current, 'webrtc')
        return
      }
      if (typeof outcome === 'object') {
        unavailable(current, outcome.code)
        return
      }
      // Leave the failed peer connection behind before MSE takes the element.
      teardown()
    }
    tryMse(current, video)
  }

  return { status, transport, problemCode, start, stop }
}
