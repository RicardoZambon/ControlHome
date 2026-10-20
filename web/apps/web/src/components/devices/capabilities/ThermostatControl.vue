<script setup lang="ts">
import type { ValueSchema } from '@controlhome/api-client'
import {
  Button,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  ToggleGroup,
  ToggleGroupItem,
  cn,
} from '@controlhome/ui'
import { Droplets, Fan, Flame, Gauge, Minus, Plus, Power, Snowflake } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, useId, type Component } from 'vue'
import { useCapabilityState } from '../../../composables/useStateValue'
import { allowedValues, numberBounds, snapNumber } from '../../schema/schema'
import { measurement } from '../format'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `thermostat`: a 270° dial for the target temperature (drag it, use the − / + buttons or the arrow keys), the room
 * temperature as a marker on the dial, the modes as a segmented choice and the fan speed. The target's range follows the
 * endpoint's constraints for the current mode (an AC that heats only to 26 °C).
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, locale } = control
const id = useId()

const TARGET: Extract<ValueSchema, { type: 'number' }> = {
  type: 'number',
  min: 5,
  max: 35,
  step: 0.5,
  unit: '°C',
}
const MODES = ['off', 'cool', 'heat', 'auto', 'dry', 'fan']
const FAN_MODES = ['auto', 'quiet', 'low', 'medium', 'high', 'turbo']
const MODE_ICONS: Record<string, Component> = {
  off: Power,
  cool: Snowflake,
  heat: Flame,
  auto: Gauge,
  dry: Droplets,
  fan: Fan,
}
const ARC_TONES: Record<string, string> = {
  cool: 'stroke-state-info',
  heat: 'stroke-state-warn',
  auto: 'stroke-primary',
  dry: 'stroke-state-info',
  fan: 'stroke-muted-foreground',
}

const schemaOf = (key: string) =>
  control.definition.value?.attributes.find((entry) => entry.key === key)?.schema

const bounds = computed(() => numberBounds(TARGET, control.constraint('targetTemperature')))
const min = computed(() => bounds.value.min ?? 5)
const max = computed(() => bounds.value.max ?? 35)
const step = computed(() => bounds.value.step ?? 0.5)

const mode = computed(() => control.state.string('mode'))
const target = computed(() => control.state.number('targetTemperature'))
const current = computed(() => control.state.number('currentTemperature'))
const fanMode = computed(() => control.state.string('fanMode'))
const modes = computed(() =>
  allowedValues(schemaOf('mode') ?? { type: 'enum', values: MODES }, control.constraint('mode')),
)
const fanModes = computed(() =>
  control.state.has('fanMode')
    ? allowedValues(schemaOf('fanMode') ?? { type: 'enum', values: FAN_MODES }, control.constraint('fanMode'))
    : [],
)

// An AC's power switch: while it is off, the dial rests.
const powerSwitch = useCapabilityState(() =>
  props.endpoint.capabilities.find((capability) => capability.capabilityId === 'switch'),
)
const running = computed(
  () => powerSwitch.bool('on') !== false && mode.value !== undefined && mode.value !== 'off',
)

// --- Target: a draft while it is being changed, sent once the gesture ends --------------------------------------------
const draft = ref<number>()
const shown = computed(() => draft.value ?? target.value)
let commitTimer: ReturnType<typeof setTimeout> | undefined

function snap(value: number): number {
  return snapNumber(value, { ...bounds.value, min: min.value, max: max.value })
}

function commit(): void {
  clearTimeout(commitTimer)
  commitTimer = undefined
  const value = draft.value
  if (value === undefined) return
  draft.value = undefined
  if (value !== target.value) void control.send('setTargetTemperature', { targetTemperature: value })
}

function nudge(direction: 1 | -1): void {
  if (!control.enabled.value) return
  draft.value = snap((shown.value ?? (min.value + max.value) / 2) + direction * step.value)
  clearTimeout(commitTimer)
  commitTimer = setTimeout(commit, 700)
}

onBeforeUnmount(commit)

// --- Dial geometry (viewBox 240 × 240) --------------------------------------------------------------------------------
const CENTER = 120
const RADIUS = 96
const START = 135
const SWEEP = 270

function point(angle: number): { x: number; y: number } {
  const radians = (angle * Math.PI) / 180
  return { x: CENTER + RADIUS * Math.cos(radians), y: CENTER + RADIUS * Math.sin(radians) }
}

function arc(from: number, to: number): string {
  const a = point(from)
  const b = point(to)
  return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A ${RADIUS} ${RADIUS} 0 ${to - from > 180 ? 1 : 0} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`
}

const fraction = (value: number) =>
  Math.min(1, Math.max(0, (value - min.value) / (max.value - min.value || 1)))
const angleOf = (value: number) => START + SWEEP * fraction(value)

const trackPath = arc(START, START + SWEEP)
const valuePath = computed(() =>
  shown.value === undefined || fraction(shown.value) < 0.002 ? '' : arc(START, angleOf(shown.value)),
)
const knob = computed(() => (shown.value === undefined ? undefined : point(angleOf(shown.value))))
const marker = computed(() =>
  current.value !== undefined && current.value >= min.value && current.value <= max.value
    ? point(angleOf(current.value))
    : undefined,
)
const arcTone = computed(() =>
  running.value ? (ARC_TONES[mode.value ?? ''] ?? 'stroke-primary') : 'stroke-state-off',
)

const svg = ref<SVGSVGElement>()
const dragging = ref(false)

function valueAt(event: PointerEvent): number | undefined {
  const box = svg.value?.getBoundingClientRect()
  if (!box || box.width === 0) return undefined
  const scale = box.width / 240
  const x = (event.clientX - box.left) / scale - CENTER
  const y = (event.clientY - box.top) / scale - CENTER
  let angle = (Math.atan2(y, x) * 180) / Math.PI
  if (angle < 0) angle += 360
  let along = (angle - START + 360) % 360
  // In the gap at the bottom: the nearer end.
  if (along > SWEEP) along = along > SWEEP + (360 - SWEEP) / 2 ? 0 : SWEEP
  return snap(min.value + (along / SWEEP) * (max.value - min.value))
}

function onPointerDown(event: PointerEvent): void {
  if (!control.enabled.value || event.button !== 0) return
  ;(event.currentTarget as Element).setPointerCapture?.(event.pointerId)
  dragging.value = true
  clearTimeout(commitTimer)
  const value = valueAt(event)
  if (value !== undefined) draft.value = value
}

function onPointerMove(event: PointerEvent): void {
  if (!dragging.value) return
  const value = valueAt(event)
  if (value !== undefined) draft.value = value
}

function onPointerUp(): void {
  if (!dragging.value) return
  dragging.value = false
  commit()
}

function onKeydown(event: KeyboardEvent): void {
  if (!control.enabled.value) return
  const keys: Record<string, () => void> = {
    ArrowUp: () => nudge(1),
    ArrowRight: () => nudge(1),
    ArrowDown: () => nudge(-1),
    ArrowLeft: () => nudge(-1),
    PageUp: () => {
      draft.value = snap((shown.value ?? min.value) + 1)
      commit()
    },
    PageDown: () => {
      draft.value = snap((shown.value ?? max.value) - 1)
      commit()
    },
    Home: () => {
      draft.value = min.value
      commit()
    },
    End: () => {
      draft.value = max.value
      commit()
    },
  }
  const action = keys[event.key]
  if (!action) return
  event.preventDefault()
  action()
}

const targetParts = computed(() =>
  shown.value === undefined ? undefined : measurement(shown.value, '°C', locale.value),
)
const modeTitle = computed(() =>
  running.value ? t(`device.thermostat.action.${mode.value}`) : t('device.thermostat.action.off'),
)
const fanLabelId = `${id}-fan`
</script>

<template>
  <div
    class="grid items-center gap-8 md:grid-cols-[minmax(0,20rem)_minmax(0,1fr)]"
    data-slot="thermostat-control"
  >
    <!-- The dial -->
    <div class="relative mx-auto aspect-square w-full max-w-72">
      <div
        role="slider"
        :tabindex="control.enabled.value ? 0 : -1"
        :aria-label="t('device.thermostat.target', { name: control.name.value })"
        :aria-valuemin="min"
        :aria-valuemax="max"
        :aria-valuenow="shown ?? min"
        :aria-valuetext="targetParts ? `${targetParts.value} ${targetParts.unit}` : t('device.noValue')"
        :aria-disabled="!control.enabled.value || undefined"
        class="absolute inset-0 rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-4 focus-visible:ring-offset-card"
        @keydown="onKeydown"
      >
        <svg
          ref="svg"
          viewBox="0 0 240 240"
          :class="
            cn(
              'size-full touch-none select-none',
              control.enabled.value ? 'cursor-grab' : 'cursor-not-allowed',
              dragging && 'cursor-grabbing',
            )
          "
          aria-hidden="true"
          @pointerdown="onPointerDown"
          @pointermove="onPointerMove"
          @pointerup="onPointerUp"
          @pointercancel="onPointerUp"
        >
          <path :d="trackPath" class="fill-none stroke-muted" stroke-width="16" stroke-linecap="round" />
          <path
            v-if="valuePath"
            :d="valuePath"
            :class="cn('fill-none transition-[stroke] duration-500', arcTone)"
            stroke-width="16"
            stroke-linecap="round"
          />
          <circle
            v-if="marker"
            :cx="marker.x"
            :cy="marker.y"
            r="4"
            class="fill-foreground stroke-card"
            stroke-width="2"
          />
          <circle
            v-if="knob"
            :cx="knob.x"
            :cy="knob.y"
            r="13"
            :class="cn('fill-card transition-[stroke] duration-500', arcTone)"
            stroke-width="5"
          />
        </svg>
      </div>

      <!-- The reading in the middle -->
      <div
        :class="
          cn(
            'pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-1 text-center transition-opacity',
            !running && 'opacity-70',
          )
        "
      >
        <span class="eyebrow">{{ modeTitle }}</span>
        <span class="flex items-start font-serif leading-none font-medium tracking-tight" aria-hidden="true">
          <span class="text-6xl tabular-nums">{{ targetParts?.value ?? t('device.noValue') }}</span>
          <span v-if="targetParts" class="mt-1 text-xl text-muted-foreground">{{ targetParts.unit }}</span>
        </span>
        <span v-if="current !== undefined" class="text-sm text-muted-foreground tabular-nums">
          {{ t('device.thermostat.now', { temperature: control.measure(current, '°C') }) }}
        </span>
      </div>

      <!-- − / + in the gap at the bottom of the dial -->
      <div class="absolute inset-x-0 bottom-1 flex justify-between px-[11%]">
        <Button
          variant="outline"
          size="icon-lg"
          class="rounded-full bg-card"
          :disabled="!control.enabled.value || (shown !== undefined && shown <= min)"
          :aria-label="t('device.thermostat.lower')"
          @click="nudge(-1)"
        >
          <Minus aria-hidden="true" />
        </Button>
        <Button
          variant="outline"
          size="icon-lg"
          class="rounded-full bg-card"
          :disabled="!control.enabled.value || (shown !== undefined && shown >= max)"
          :aria-label="t('device.thermostat.raise')"
          @click="nudge(1)"
        >
          <Plus aria-hidden="true" />
        </Button>
      </div>
    </div>

    <!-- Mode and fan -->
    <div class="grid content-center gap-6">
      <div class="grid gap-3">
        <span :id="`${id}-mode`" class="text-sm font-medium">{{ control.attributeLabel('mode') }}</span>
        <ToggleGroup
          type="single"
          variant="outline"
          :spacing="2"
          :model-value="mode"
          :disabled="!control.enabled.value"
          :aria-labelledby="`${id}-mode`"
          class="grid w-full grid-cols-3 gap-2"
          @update:model-value="(value) => value && value !== mode && control.send('setMode', { mode: value })"
        >
          <ToggleGroupItem
            v-for="option in modes"
            :key="option"
            :value="option"
            class="h-auto min-h-14 flex-col gap-1 py-2 text-xs data-[state=on]:border-primary"
          >
            <component :is="MODE_ICONS[option] ?? Gauge" class="size-5" aria-hidden="true" />
            {{ control.valueLabel('mode', option) }}
          </ToggleGroupItem>
        </ToggleGroup>
      </div>

      <div v-if="fanModes.length > 0" class="grid gap-3">
        <Label :id="fanLabelId" :for="`${id}-fan-trigger`">{{ control.attributeLabel('fanMode') }}</Label>
        <Select
          :model-value="fanMode"
          :disabled="!control.enabled.value"
          @update:model-value="(value) => value && control.send('setFanMode', { fanMode: value })"
        >
          <SelectTrigger :id="`${id}-fan-trigger`" class="w-full">
            <span class="flex min-w-0 items-center gap-2">
              <Fan class="size-4" aria-hidden="true" />
              <SelectValue :placeholder="t('device.noValue')" />
            </span>
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="option in fanModes" :key="option" :value="option">
              {{ control.valueLabel('fanMode', option) }}
            </SelectItem>
          </SelectContent>
        </Select>
      </div>
    </div>
  </div>
</template>
