<script setup lang="ts">
import type { EndpointCapabilityDto, EndpointDto } from '@controlhome/api-client'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@controlhome/ui'
import { Cctv, Play } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useCapabilityState } from '../../../composables/useStateValue'
import CameraPlayer from './CameraPlayer.vue'
import MotionBadge from './MotionBadge.vue'
import { useCameraSnapshot } from './useCameraSnapshot'

/**
 * A camera's picture on its tile: the latest snapshot (refreshed every 10 s while the page is visible) with a motion
 * badge. When the camera has a stream, activating the picture opens live view in a dialog.
 */
const props = defineProps<{
  endpoint: EndpointDto
  camera: EndpointCapabilityDto
  /** The endpoint's display name. */
  name: string
  /** The device reports motion right now. */
  motion: boolean
  /** The device is offline: no refreshing, no live view. */
  offline: boolean
}>()

const { t } = useI18n()
const open = ref(false)
const state = useCapabilityState(() => props.camera)
const stream = computed(() => state.bool('streamAvailable') === true && !props.offline)
const snapshot = useCameraSnapshot(
  () => props.endpoint.id,
  () => !open.value && !props.offline,
)
</script>

<template>
  <!-- Above the tile's stretched link only when it opens live view; a plain picture opens the device page -->
  <div
    :class="['relative aspect-video w-full overflow-hidden rounded-xl border bg-muted/60', stream && 'z-10']"
    data-slot="tile-camera"
  >
    <button
      v-if="stream"
      type="button"
      class="group/camera absolute inset-0 grid size-full place-items-center outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
      data-slot="tile-camera-watch"
      @click="open = true"
    >
      <img
        v-if="snapshot.url.value"
        :src="snapshot.url.value"
        alt=""
        class="absolute inset-0 size-full object-cover"
      />
      <Cctv v-else class="size-8 text-muted-foreground" aria-hidden="true" />
      <span
        class="absolute right-2 bottom-2 flex size-9 items-center justify-center rounded-full bg-card/90 text-foreground shadow-sm transition-transform group-hover/camera:scale-105"
        aria-hidden="true"
      >
        <Play class="size-4" />
      </span>
      <span class="sr-only">{{ t('device.camera.watchLiveOf', { name }) }}</span>
    </button>
    <template v-else>
      <img
        v-if="snapshot.url.value"
        :src="snapshot.url.value"
        :alt="t('device.camera.snapshotAlt', { name })"
        class="absolute inset-0 size-full object-cover"
      />
      <span v-else class="absolute inset-0 grid place-items-center" aria-hidden="true">
        <Cctv class="size-8 text-muted-foreground" />
      </span>
    </template>
    <MotionBadge v-if="motion" class="pointer-events-none absolute top-2 left-2" />

    <Dialog v-model:open="open">
      <DialogContent :close-label="t('common.close')" class="gap-3 p-4 sm:max-w-3xl sm:p-6">
        <DialogHeader class="pr-8">
          <DialogTitle class="truncate">{{ name }}</DialogTitle>
          <DialogDescription class="sr-only">{{
            t('device.camera.live.dialogDescription')
          }}</DialogDescription>
        </DialogHeader>
        <CameraPlayer
          v-if="open"
          :endpoint-id="endpoint.id"
          :name="name"
          :snapshot-url="snapshot.url.value"
          :motion="motion"
        />
      </DialogContent>
    </Dialog>
  </div>
</template>
