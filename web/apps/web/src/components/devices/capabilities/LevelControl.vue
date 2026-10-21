<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { computed, ref } from 'vue'
import { useCapabilityState } from '../../../composables/useStateValue'
import { numberBounds, snapNumber } from '../../schema/schema'
import FillSlider from '../controls/FillSlider.vue'
import { KELVIN_STOPS, lightColorOf, lightLayers } from '../lightColor'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `level`: brightness (or any 0–100% level) as a large fill slider with quick presets. The card names it, so the
 * control shows just the value. The fill is the light itself: warm white, or the bulb's own color.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const bounds = computed(() =>
  numberBounds(
    { type: 'number', min: 0, max: 100, step: 1, unit: '%', integer: true },
    control.constraint('level'),
  ),
)
const level = computed(() => control.state.number('level'))
/** The value under the finger while dragging. */
const dragged = ref<number | undefined>()
const shown = computed(() => dragged.value ?? level.value)
const presets = computed(() => [25, 50, 75, 100].map((value) => snapNumber(value, bounds.value)))

// The fill: a color bulb's color while it shows one, else warm white.
const color = useCapabilityState(() =>
  props.endpoint.capabilities.find(
    (capability) => capability.capabilityId === 'color' && !capability.retired,
  ),
)
const fillClass = computed(() => {
  const current = lightColorOf({
    hue: color.number('hue'),
    saturation: color.number('saturation'),
    colorTemperature: color.number('colorTemperature'),
  })
  return current?.mode === 'color' ? lightLayers(current).solid : KELVIN_STOPS[0].bg
})

function commit(value: number): void {
  dragged.value = undefined
  void control.send('setLevel', { level: snapNumber(value, bounds.value) })
}
</script>

<template>
  <div class="grid gap-4" data-slot="level-control">
    <p
      class="font-serif text-4xl leading-none font-medium tabular-nums"
      aria-hidden="true"
      data-slot="level-value"
    >
      {{ shown === undefined ? t('device.noValue') : control.measure(shown, '%') }}
    </p>
    <FillSlider
      :model-value="level"
      :min="bounds.min ?? 0"
      :max="bounds.max ?? 100"
      :step="bounds.step ?? 1"
      :label="t('device.level.label', { name: control.name.value })"
      :value-text="shown === undefined ? undefined : control.measure(shown, '%')"
      :disabled="!control.enabled.value"
      :fill-class="fillClass"
      @update:model-value="(value) => (dragged = value)"
      @commit="commit"
    />
    <div class="grid grid-cols-4 gap-2">
      <Button
        v-for="preset in presets"
        :key="preset"
        variant="outline"
        :disabled="!control.enabled.value"
        :aria-pressed="level === preset"
        :class="
          cn('px-2 tabular-nums', level === preset && 'border-foreground/50 bg-accent text-accent-foreground')
        "
        @click="commit(preset)"
      >
        {{ control.measure(preset, '%') }}
      </Button>
    </div>
  </div>
</template>
