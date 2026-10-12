<script setup lang="ts">
import type { FloorBackgroundDto } from '@controlhome/api-client'
import { computed, ref, watch } from 'vue'

/**
 * The traced plan image under the rooms: placed at its offset and scaled by its cm-per-pixel calibration. The image's
 * pixel size is read once it has loaded (the floor only stores the calibration), and reported with `loaded`. It is a
 * tracing aid: the editor shows it at the floor's opacity, the view only on request and fainter (`maxOpacity`).
 */
const props = defineProps<{ background: FloorBackgroundDto; maxOpacity?: number }>()
const emit = defineEmits<{ loaded: [size: { widthPx: number; heightPx: number }]; failed: [] }>()

const href = computed(() => `/api/files/${encodeURIComponent(props.background.fileId)}`)
const size = ref<{ widthPx: number; heightPx: number } | null>(null)
const opacity = computed(() =>
  props.maxOpacity === undefined
    ? props.background.opacity
    : Math.min(props.background.opacity, props.maxOpacity),
)

watch(
  href,
  (url, _previous, onCleanup) => {
    size.value = null
    if (typeof Image === 'undefined') return
    const image = new Image()
    let active = true
    onCleanup(() => (active = false))
    image.onload = () => {
      if (!active) return
      size.value = { widthPx: image.naturalWidth, heightPx: image.naturalHeight }
      emit('loaded', size.value)
    }
    image.onerror = () => {
      if (active) emit('failed')
    }
    image.src = url
  },
  { immediate: true },
)
</script>

<template>
  <g data-slot="floor-background" class="pointer-events-none" aria-hidden="true">
    <image
      v-if="size"
      :href="href"
      :x="background.offsetXCm"
      :y="background.offsetYCm"
      :width="size.widthPx * background.cmPerPx"
      :height="size.heightPx * background.cmPerPx"
      :opacity="opacity"
      preserveAspectRatio="none"
      class="dark:hue-rotate-180 dark:invert"
    />
  </g>
</template>
