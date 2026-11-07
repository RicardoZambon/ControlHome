<script setup lang="ts">
import { Badge, Button, toast } from '@controlhome/ui'
import { Camera, Cctv, Image, Play } from '@lucide/vue'
import { computed, ref } from 'vue'
import CameraPlayer from '../camera/CameraPlayer.vue'
import MotionBadge from '../camera/MotionBadge.vue'
import { useCameraSnapshot } from '../camera/useCameraSnapshot'
import { useDeviceMotion } from '../camera/useDeviceMotion'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `camera`: the latest snapshot (refreshed every 10 s while the page is visible), "Watch live" to swap it for the live
 * player, and the snapshot command. Without a picture it shows a placeholder. A motion badge shows while the device
 * reports motion.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const stream = computed(() => control.state.bool('streamAvailable'))
const watching = ref(false)
const snapshot = useCameraSnapshot(
  () => props.endpoint.id,
  () => !watching.value && !control.offline.value,
)
const motion = useDeviceMotion(() => props.device)

async function takeSnapshot(): Promise<void> {
  if ((await control.send('snapshot')) !== 'confirmed') return
  toast.success(t('device.camera.snapshotTaken'))
  void snapshot.refresh()
}
</script>

<template>
  <div class="grid min-w-0 gap-4" data-slot="camera-control" :data-watching="watching || undefined">
    <CameraPlayer
      v-if="watching"
      :endpoint-id="endpoint.id"
      :name="control.name.value"
      :snapshot-url="snapshot.url.value"
      :motion="motion"
    />
    <div
      v-else
      class="relative grid aspect-video w-full place-items-center overflow-hidden rounded-xl border bg-muted/60"
      :class="
        !snapshot.url.value &&
        'bg-[linear-gradient(to_right,var(--border)_1px,transparent_1px),linear-gradient(to_bottom,var(--border)_1px,transparent_1px)] bg-size-[2rem_2rem]'
      "
      data-slot="camera-snapshot"
    >
      <img
        v-if="snapshot.url.value"
        :src="snapshot.url.value"
        :alt="t('device.camera.snapshotAlt', { name: control.name.value })"
        class="absolute inset-0 size-full object-cover"
      />
      <div v-else class="grid place-items-center gap-2 rounded-xl bg-card/90 px-5 py-4 text-center shadow-sm">
        <Cctv class="size-8 text-muted-foreground" aria-hidden="true" />
        <p class="max-w-56 text-sm text-muted-foreground">
          {{
            snapshot.loading.value && !snapshot.problemCode.value
              ? t('common.loading')
              : t('device.camera.placeholder')
          }}
        </p>
      </div>
      <Badge variant="outline" class="absolute top-2 left-2 bg-card/90">
        <span :class="['size-2 rounded-full', stream ? 'bg-state-on' : 'bg-state-off']" aria-hidden="true" />
        {{ stream ? t('device.camera.streamAvailable') : t('device.camera.noStream') }}
      </Badge>
      <MotionBadge v-if="motion" class="absolute top-2 right-2" />
    </div>

    <div class="flex flex-wrap items-center justify-between gap-2">
      <Button v-if="watching" variant="outline" data-slot="camera-stop" @click="watching = false">
        <Image aria-hidden="true" />
        {{ t('device.camera.showSnapshot') }}
      </Button>
      <Button
        v-else-if="stream"
        data-slot="camera-watch"
        :disabled="control.offline.value"
        @click="watching = true"
      >
        <Play aria-hidden="true" />
        {{ t('device.camera.watchLive') }}
      </Button>
      <span v-else />
      <Button
        v-if="control.canControl.value && control.hasCommand('snapshot')"
        variant="outline"
        :disabled="!control.enabled.value"
        :loading="control.pending('snapshot')"
        @click="takeSnapshot"
      >
        <Camera aria-hidden="true" />
        {{ control.commandLabel('snapshot') }}
      </Button>
    </div>
  </div>
</template>
