<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { SliderRange, SliderRoot, SliderThumb, SliderTrack } from 'reka-ui'
import { ref, watch, type HTMLAttributes } from 'vue'

/**
 * The large touch slider of device controls (brightness, volume, position, color): a 44 px track that fills up to the
 * value, or shows a gradient (`trackClass`, e.g. `bg-spectrum`) with a ring thumb. While dragging it shows the dragged
 * value; `commit` fires once on release (or per key press), so a control sends one command per gesture.
 * Built on reka-ui's slider (keyboard, pointer and ARIA come from it).
 */
const props = withDefaults(
  defineProps<{
    modelValue?: number
    min?: number
    max?: number
    step?: number
    /** Accessible name of the slider. */
    label: string
    /** Spoken value ("80 %"). */
    valueText?: string
    disabled?: boolean
    /** A gradient track instead of the fill (color sliders). */
    trackClass?: string
    /** Fill color class (default primary). */
    fillClass?: string
    class?: HTMLAttributes['class']
  }>(),
  { min: 0, max: 100, step: 1 },
)
const emit = defineEmits<{ 'update:modelValue': [value: number]; commit: [value: number] }>()

const local = ref<number>(props.modelValue ?? props.min)
const dragging = ref(false)
watch(
  () => props.modelValue,
  (value) => {
    if (!dragging.value && value !== undefined) local.value = value
  },
)

function onUpdate(values: number[] | undefined): void {
  const value = values?.[0]
  if (value === undefined) return
  dragging.value = true
  local.value = value
  emit('update:modelValue', value)
}

function onCommit(values: number[]): void {
  dragging.value = false
  const value = values[0]
  if (value !== undefined) emit('commit', value)
}
</script>

<template>
  <SliderRoot
    :model-value="[local]"
    :min="min"
    :max="max"
    :step="step"
    :disabled="disabled"
    data-slot="fill-slider"
    :class="
      cn(
        'relative flex h-11 w-full touch-none items-center select-none data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50',
        props.class,
      )
    "
    @update:model-value="onUpdate"
    @value-commit="onCommit"
    @pointerup="dragging = false"
  >
    <SliderTrack
      :class="
        cn('relative h-full w-full grow overflow-hidden rounded-xl', trackClass ?? 'bg-muted shadow-inner')
      "
    >
      <SliderRange
        v-if="!trackClass"
        :class="cn('absolute h-full transition-[width] duration-150', fillClass ?? 'bg-primary')"
      />
    </SliderTrack>
    <SliderThumb
      :aria-label="label"
      :aria-valuetext="valueText"
      :class="
        cn(
          'block shrink-0 rounded-full shadow-md outline-none before:absolute before:-inset-3 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
          trackClass
            ? 'size-8 border-4 border-background bg-transparent ring-1 ring-border'
            : 'h-7 w-1.5 bg-background ring-1 ring-border',
        )
      "
    />
  </SliderRoot>
</template>
