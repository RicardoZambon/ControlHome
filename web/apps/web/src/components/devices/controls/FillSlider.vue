<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { SliderRange, SliderRoot, SliderThumb, SliderTrack } from 'reka-ui'
import { ref, watch, type HTMLAttributes } from 'vue'

/**
 * The large touch slider of device controls (brightness, volume, position, color): a 44 px track that fills up to the
 * value with a 20 px pill handle, or shows a gradient (`trackClass`, e.g. `bg-spectrum`) with a ring handle. While
 * dragging it shows the dragged value; `commit` fires once on release (or per key press), so a control sends one
 * command per gesture. Built on reka-ui's slider (keyboard, mouse and ARIA come from it).
 *
 * Touch: the page still scrolls when a vertical swipe starts on the track (`touch-action: pan-y`). A finger on the
 * track changes the value only once it has moved {@link TOUCH_SLOP_PX} sideways, or when it lifts without moving (a
 * deliberate tap); the handle itself drags at once.
 */
const props = withDefaults(
  defineProps<{
    modelValue?: number
    min?: number
    max?: number
    step?: number
    /** Accessible name of the slider. */
    label: string
    /** Spoken value ("80%"). */
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

/** How far a finger must move sideways on the track before the slider follows it. */
const TOUCH_SLOP_PX = 8

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

// --- Touch on the track ------------------------------------------------------------------------------------------------
const wrapper = ref<HTMLElement>()
let touch: { id: number; x: number; y: number; following: boolean } | undefined

const decimals = (value: number) => (String(value).split('.')[1] ?? '').length

/** The value under a finger at `clientX` (the handle stays inside the track, as reka-ui's "contain" alignment does). */
function valueAt(clientX: number): number | undefined {
  const root = wrapper.value?.querySelector<HTMLElement>('[data-slot="fill-slider"]')
  if (!root) return undefined
  const rect = root.getBoundingClientRect()
  const thumb = root.querySelector<HTMLElement>('[data-slot="fill-slider-thumb"]')?.clientWidth ?? 0
  const span = rect.width - thumb
  if (span <= 0) return undefined
  const fraction = Math.min(1, Math.max(0, (clientX - rect.left - thumb / 2) / span))
  const raw = props.min + fraction * (props.max - props.min)
  const stepped = props.min + Math.round((raw - props.min) / props.step) * props.step
  const clamped = Math.min(props.max, Math.max(props.min, stepped))
  return Number(clamped.toFixed(Math.max(decimals(props.step), decimals(props.min))))
}

function follow(clientX: number): void {
  const value = valueAt(clientX)
  if (value === undefined) return
  dragging.value = true
  local.value = value
  emit('update:modelValue', value)
}

function onPointerDown(event: PointerEvent): void {
  if (event.pointerType !== 'touch' || props.disabled) return
  if ((event.target as Element | null)?.closest('[data-slot="fill-slider-thumb"]')) return
  // The track: the slider waits to see whether the finger scrolls the page or slides.
  event.stopPropagation()
  touch = { id: event.pointerId, x: event.clientX, y: event.clientY, following: false }
}

function onPointerMove(event: PointerEvent): void {
  if (!touch || event.pointerId !== touch.id) return
  event.stopPropagation()
  if (!touch.following) {
    const dx = Math.abs(event.clientX - touch.x)
    const dy = Math.abs(event.clientY - touch.y)
    if (dx < TOUCH_SLOP_PX || dx <= dy) return
    touch.following = true
    wrapper.value?.setPointerCapture?.(event.pointerId)
  }
  follow(event.clientX)
}

function onPointerUp(event: PointerEvent): void {
  if (!touch || event.pointerId !== touch.id) return
  event.stopPropagation()
  // A tap (the finger lifted where it landed) sets the value there; a swipe that did not scroll the page does nothing.
  const tapped =
    Math.abs(event.clientX - touch.x) < TOUCH_SLOP_PX && Math.abs(event.clientY - touch.y) < TOUCH_SLOP_PX
  if (!touch.following && tapped) follow(touch.x)
  touch = undefined
  if (!dragging.value) return
  dragging.value = false
  emit('commit', local.value)
}

function onPointerCancel(event: PointerEvent): void {
  if (!touch || event.pointerId !== touch.id) return
  event.stopPropagation()
  // The browser took the gesture (a scroll): nothing changes.
  const followed = touch.following
  touch = undefined
  if (!followed) return
  dragging.value = false
  if (props.modelValue !== undefined) {
    local.value = props.modelValue
    emit('update:modelValue', props.modelValue)
  }
}
</script>

<template>
  <div
    ref="wrapper"
    :class="cn('w-full touch-pan-y', props.class)"
    @pointerdown.capture="onPointerDown"
    @pointermove.capture="onPointerMove"
    @pointerup.capture="onPointerUp"
    @pointercancel.capture="onPointerCancel"
  >
    <SliderRoot
      :model-value="[local]"
      :min="min"
      :max="max"
      :step="step"
      :disabled="disabled"
      data-slot="fill-slider"
      class="relative flex h-11 w-full items-center select-none data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50"
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
        data-slot="fill-slider-thumb"
        :class="
          cn(
            'block shrink-0 cursor-grab touch-none rounded-full shadow-md outline-none before:absolute before:-inset-3 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background active:cursor-grabbing',
            trackClass
              ? 'size-8 border-4 border-background bg-transparent ring-1 ring-border'
              : 'h-8 w-5 border-2 border-background bg-background ring-1 ring-foreground/25',
          )
        "
      />
    </SliderRoot>
  </div>
</template>
