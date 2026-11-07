<script setup lang="ts">
import { Badge, Button, ToggleGroup, ToggleGroupItem } from '@controlhome/ui'
import { LoaderCircle, RotateCw } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import MotionBadge from './MotionBadge.vue'
import { useCameraLive, type LiveQuality } from './useCameraLive'

/**
 * A camera's live view: WebRTC first, MSE over the app's WebSocket as the fallback (see `useCameraLive`). Muted,
 * autoplaying and inline. While it connects or when live video is not available, the last snapshot shows behind the
 * message. Everything is closed on unmount and while the page is hidden, and reconnects when it is visible again.
 */
const props = withDefaults(
  defineProps<{
    endpointId: string
    /** The camera's name, for the video's accessible label. */
    name: string
    /** The last snapshot (object URL), shown until the first frame and behind "unavailable". */
    snapshotUrl?: string
    /** The device reports motion right now. */
    motion?: boolean
    initialQuality?: LiveQuality
  }>(),
  { initialQuality: 'main' },
)

const { t, te } = useI18n()
const video = ref<HTMLVideoElement | null>(null)
const quality = ref<LiveQuality>(props.initialQuality)
const live = useCameraLive({ endpointId: () => props.endpointId, quality, video })
const { status, transport } = live

const transportLabel = computed(() =>
  transport.value ? t(`device.camera.live.transport.${transport.value}`) : '',
)
const unavailableMessage = computed(() => {
  const code = live.problemCode.value
  return code && te(`errors.${code}`) ? t(`errors.${code}`) : t('errors.unknown')
})
/** What the polite live region says. */
const announcement = computed(() => {
  switch (status.value) {
    case 'connecting':
      return t('device.camera.live.connecting')
    case 'live':
      return t('device.camera.live.liveVia', { transport: transportLabel.value })
    case 'unavailable':
      return unavailableMessage.value
    case 'error':
      return t('device.camera.live.failed')
    default:
      return ''
  }
})
const showBackdrop = computed(() => Boolean(props.snapshotUrl) && status.value !== 'live')

function onQuality(value: unknown): void {
  if (value !== 'main' && value !== 'sub') return
  quality.value = value
}

watch([quality, () => props.endpointId], () => void live.start())

function onVisibility(): void {
  if (document.visibilityState === 'hidden') live.stop()
  else if (status.value === 'idle') void live.start()
}

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibility)
  if (document.visibilityState !== 'hidden') void live.start()
})

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibility)
  live.stop()
})
</script>

<template>
  <div class="grid min-w-0 gap-3" data-slot="camera-player" :data-status="status" :data-transport="transport">
    <div class="relative aspect-video w-full overflow-hidden rounded-xl border bg-muted">
      <img
        v-if="showBackdrop"
        :src="snapshotUrl"
        alt=""
        class="absolute inset-0 size-full object-cover"
        :class="status === 'connecting' ? 'opacity-80' : 'opacity-40'"
        data-slot="camera-backdrop"
      />
      <video
        ref="video"
        class="absolute inset-0 size-full object-contain transition-opacity duration-300"
        :class="status === 'live' ? 'opacity-100' : 'opacity-0'"
        :aria-label="t('device.camera.live.videoLabel', { name })"
        muted
        autoplay
        playsinline
        disablepictureinpicture
      />

      <!-- One polite live region: connecting, live (and how), or why there is no video -->
      <div
        role="status"
        aria-live="polite"
        class="pointer-events-none absolute inset-0 grid place-items-center p-3"
        data-slot="camera-status"
      >
        <Badge
          v-if="status === 'live'"
          variant="outline"
          class="absolute top-2 left-2 bg-card/90 shadow-sm"
          data-slot="camera-live"
        >
          <span class="size-2 rounded-full bg-state-danger motion-safe:animate-pulse" aria-hidden="true" />
          {{ t('device.camera.live.badge') }}
          <span class="font-normal text-muted-foreground">· {{ transportLabel }}</span>
        </Badge>
        <p
          v-else-if="status === 'connecting'"
          class="flex items-center gap-2 rounded-lg bg-card/90 px-3 py-2 text-sm shadow-sm"
        >
          <LoaderCircle class="size-4 shrink-0 motion-safe:animate-spin" aria-hidden="true" />
          {{ t('device.camera.live.connecting') }}
        </p>
        <p
          v-else-if="status === 'unavailable' || status === 'error'"
          class="max-w-xs rounded-lg bg-card/90 px-3 py-2 text-center text-sm text-balance shadow-sm"
          data-slot="camera-message"
        >
          {{ announcement }}
        </p>
      </div>

      <MotionBadge v-if="motion" class="absolute top-2 right-2" />
    </div>

    <div class="flex flex-wrap items-center justify-between gap-2">
      <Button
        v-if="status === 'unavailable' || status === 'error'"
        variant="outline"
        size="sm"
        data-slot="camera-retry"
        @click="live.start()"
      >
        <RotateCw aria-hidden="true" />
        {{ t('common.retry') }}
      </Button>
      <span v-else />
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        :model-value="quality"
        :aria-label="t('device.camera.quality.label')"
        data-slot="camera-quality"
        @update:model-value="onQuality"
      >
        <ToggleGroupItem value="main">{{ t('device.camera.quality.main') }}</ToggleGroupItem>
        <ToggleGroupItem value="sub">{{ t('device.camera.quality.sub') }}</ToggleGroupItem>
      </ToggleGroup>
    </div>
  </div>
</template>
