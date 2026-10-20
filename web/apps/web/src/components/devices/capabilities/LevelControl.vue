<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { Sun, SunDim } from '@lucide/vue'
import { computed, ref } from 'vue'
import { numberBounds, snapNumber } from '../../schema/schema'
import FillSlider from '../controls/FillSlider.vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/** `level`: brightness (or any 0–100 % level) as a large fill slider with quick presets. */
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

function commit(value: number): void {
  dragged.value = undefined
  void control.send('setLevel', { level: snapNumber(value, bounds.value) })
}
</script>

<template>
  <div class="grid gap-4" data-slot="level-control">
    <div class="flex items-end justify-between gap-3">
      <span class="flex items-center gap-2 text-sm text-muted-foreground">
        <SunDim class="size-4" aria-hidden="true" />
        {{ control.attributeLabel('level') }}
      </span>
      <span class="font-serif text-3xl leading-none font-medium tabular-nums" aria-hidden="true">
        {{ shown === undefined ? t('device.noValue') : control.measure(shown, '%') }}
      </span>
    </div>
    <FillSlider
      :model-value="level"
      :min="bounds.min ?? 0"
      :max="bounds.max ?? 100"
      :step="bounds.step ?? 1"
      :label="t('device.level.label', { name: control.name.value })"
      :value-text="shown === undefined ? undefined : control.measure(shown, '%')"
      :disabled="!control.enabled.value"
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
          cn('px-2 tabular-nums', level === preset && 'border-primary bg-accent text-accent-foreground')
        "
        @click="commit(preset)"
      >
        <Sun v-if="preset === 100" aria-hidden="true" />
        {{ control.measure(preset, '%') }}
      </Button>
    </div>
  </div>
</template>
