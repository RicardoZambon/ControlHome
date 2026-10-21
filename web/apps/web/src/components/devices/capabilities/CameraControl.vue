<script setup lang="ts">
import { Badge, Button, toast } from '@controlhome/ui'
import { Camera, Cctv } from '@lucide/vue'
import { computed } from 'vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `camera`: a placeholder for the snapshot and live view (they arrive with the camera milestone), whether the camera
 * offers a stream, and the snapshot command.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const stream = computed(() => control.state.bool('streamAvailable'))

async function snapshot(): Promise<void> {
  if ((await control.send('snapshot')) === 'confirmed') toast.success(t('device.camera.snapshotTaken'))
}
</script>

<template>
  <div class="grid gap-4" data-slot="camera-control">
    <div
      class="relative grid aspect-video place-items-center overflow-hidden rounded-xl border bg-muted/60 bg-[linear-gradient(to_right,var(--border)_1px,transparent_1px),linear-gradient(to_bottom,var(--border)_1px,transparent_1px)] bg-size-[2rem_2rem]"
      role="img"
      :aria-label="t('device.camera.placeholder')"
    >
      <div class="grid place-items-center gap-2 rounded-xl bg-card/90 px-5 py-4 text-center shadow-sm">
        <Cctv class="size-8 text-muted-foreground" aria-hidden="true" />
        <p class="max-w-56 text-sm text-muted-foreground">{{ t('device.camera.placeholder') }}</p>
      </div>
      <Badge variant="outline" class="absolute top-3 left-3 bg-card">
        <span :class="['size-2 rounded-full', stream ? 'bg-state-on' : 'bg-state-off']" aria-hidden="true" />
        {{ stream ? t('device.camera.streamAvailable') : t('device.camera.noStream') }}
      </Badge>
    </div>
    <div v-if="control.canControl.value && control.hasCommand('snapshot')" class="flex justify-end">
      <Button
        variant="outline"
        :disabled="!control.enabled.value"
        :loading="control.pending('snapshot')"
        @click="snapshot"
      >
        <Camera aria-hidden="true" />
        {{ control.commandLabel('snapshot') }}
      </Button>
    </div>
  </div>
</template>
