<script setup lang="ts">
import { Button, ToggleGroup, ToggleGroupItem, cn } from '@controlhome/ui'
import { Palette, SunMedium } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import ColorSwatch from '../controls/ColorSwatch.vue'
import FillSlider from '../controls/FillSlider.vue'
import { hueTrackEnd, lightColorOf, type LightColor } from '../lightColor'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `color`: a hue wheel and saturation for colored light, and a warm-to-cool slider for white light, each with presets.
 * The swatch shows the light's color as it is now.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const hue = computed(() => control.state.number('hue'))
const saturation = computed(() => control.state.number('saturation'))
const kelvin = computed(() => control.state.number('colorTemperature'))
const current = computed(() =>
  lightColorOf({ hue: hue.value, saturation: saturation.value, colorTemperature: kelvin.value }),
)

const canColor = computed(() => control.hasCommand('setColor') && control.state.has('hue'))
const canWhite = computed(
  () => control.hasCommand('setColorTemperature') && control.state.has('colorTemperature'),
)
const mode = ref<'color' | 'white'>(current.value?.mode ?? (canColor.value ? 'color' : 'white'))
watch(current, (value) => {
  if (value) mode.value = value.mode
})

// Values under the finger while dragging.
const draggedHue = ref<number>()
const draggedSaturation = ref<number>()
const draggedKelvin = ref<number>()
const shownHue = computed(() => draggedHue.value ?? hue.value ?? 0)
const shownSaturation = computed(() => draggedSaturation.value ?? saturation.value ?? 100)
const shownKelvin = computed(() => draggedKelvin.value ?? kelvin.value ?? 2700)

const kelvinRange = computed(() => {
  const constraint = control.constraint('colorTemperature')
  const min = Math.min(constraint?.min ?? 2000, kelvin.value ?? Infinity)
  const max = Math.max(constraint?.max ?? 6500, kelvin.value ?? -Infinity)
  return { min, max }
})

const preview = computed<LightColor>(() =>
  mode.value === 'color'
    ? { mode: 'color', hue: shownHue.value, saturation: shownSaturation.value }
    : { mode: 'white', kelvin: shownKelvin.value },
)

const COLOR_PRESETS = [
  { key: 'red', hue: 0 },
  { key: 'orange', hue: 30 },
  { key: 'yellow', hue: 55 },
  { key: 'green', hue: 120 },
  { key: 'cyan', hue: 185 },
  { key: 'blue', hue: 230 },
  { key: 'purple', hue: 275 },
  { key: 'pink', hue: 320 },
] as const
const WHITE_PRESETS = [
  { key: 'warm', kelvin: 2700 },
  { key: 'neutral', kelvin: 4000 },
  { key: 'daylight', kelvin: 6500 },
] as const

function setColor(nextHue: number, nextSaturation: number): void {
  draggedHue.value = undefined
  draggedSaturation.value = undefined
  void control.send('setColor', { hue: Math.round(nextHue), saturation: Math.round(nextSaturation) })
}

function setWhite(next: number): void {
  draggedKelvin.value = undefined
  void control.send('setColorTemperature', { colorTemperature: Math.round(next) })
}
</script>

<template>
  <div class="grid gap-5" data-slot="color-control">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <span class="flex items-center gap-3">
        <ColorSwatch :color="preview" class="size-10 shadow-sm" :label="t('device.color.current')" />
        <span class="grid">
          <span class="text-sm text-muted-foreground">{{ t('device.color.current') }}</span>
          <span class="font-medium tabular-nums">
            {{
              mode === 'color'
                ? t('device.color.hueValue', {
                    hue: Math.round(shownHue),
                    saturation: Math.round(shownSaturation),
                  })
                : control.measure(shownKelvin, 'K')
            }}
          </span>
        </span>
      </span>
      <ToggleGroup
        v-if="canColor && canWhite"
        type="single"
        variant="outline"
        :model-value="mode"
        :aria-label="t('device.color.mode')"
        @update:model-value="(value) => value && (mode = value as 'color' | 'white')"
      >
        <ToggleGroupItem value="color" :aria-label="t('device.color.color')">
          <Palette aria-hidden="true" />
          {{ t('device.color.color') }}
        </ToggleGroupItem>
        <ToggleGroupItem value="white" :aria-label="t('device.color.white')">
          <SunMedium aria-hidden="true" />
          {{ t('device.color.white') }}
        </ToggleGroupItem>
      </ToggleGroup>
    </div>

    <template v-if="mode === 'color' && canColor">
      <div class="grid gap-2">
        <span class="text-sm text-muted-foreground">{{ control.attributeLabel('hue') }}</span>
        <FillSlider
          :model-value="hue ?? 0"
          :min="0"
          :max="359"
          :step="1"
          track-class="bg-spectrum"
          :label="control.attributeLabel('hue')"
          :value-text="`${Math.round(shownHue)}°`"
          :disabled="!control.enabled.value"
          @update:model-value="(value) => (draggedHue = value)"
          @commit="(value) => setColor(value, shownSaturation)"
        />
      </div>
      <div class="grid gap-2">
        <span class="text-sm text-muted-foreground">{{ control.attributeLabel('saturation') }}</span>
        <FillSlider
          :model-value="saturation ?? 100"
          :min="0"
          :max="100"
          :step="1"
          :track-class="cn('bg-linear-to-r from-kelvin-neutral', hueTrackEnd(shownHue))"
          :label="control.attributeLabel('saturation')"
          :value-text="control.measure(shownSaturation, '%')"
          :disabled="!control.enabled.value"
          @update:model-value="(value) => (draggedSaturation = value)"
          @commit="(value) => setColor(shownHue, value)"
        />
      </div>
      <div class="flex flex-wrap gap-2" role="group" :aria-label="t('device.color.presets')">
        <Button
          v-for="preset in COLOR_PRESETS"
          :key="preset.key"
          variant="outline"
          size="icon"
          class="rounded-full"
          :disabled="!control.enabled.value"
          :aria-label="t(`device.color.preset.${preset.key}`)"
          @click="setColor(preset.hue, 100)"
        >
          <ColorSwatch :color="{ mode: 'color', hue: preset.hue, saturation: 100 }" class="size-6" />
        </Button>
      </div>
    </template>

    <template v-else-if="canWhite">
      <div class="grid gap-2">
        <span class="flex items-center justify-between text-sm text-muted-foreground">
          <span>{{ t('device.color.warm') }}</span>
          <span>{{ t('device.color.cool') }}</span>
        </span>
        <FillSlider
          :model-value="kelvin ?? 2700"
          :min="kelvinRange.min"
          :max="kelvinRange.max"
          :step="50"
          track-class="bg-kelvin"
          :label="control.attributeLabel('colorTemperature')"
          :value-text="control.measure(shownKelvin, 'K')"
          :disabled="!control.enabled.value"
          @update:model-value="(value) => (draggedKelvin = value)"
          @commit="setWhite"
        />
      </div>
      <div class="flex flex-wrap gap-2" role="group" :aria-label="t('device.color.presets')">
        <Button
          v-for="preset in WHITE_PRESETS"
          :key="preset.key"
          variant="outline"
          :disabled="!control.enabled.value"
          @click="setWhite(preset.kelvin)"
        >
          <ColorSwatch :color="{ mode: 'white', kelvin: preset.kelvin }" class="size-4" />
          {{ t(`device.color.preset.${preset.key}`) }}
        </Button>
      </div>
    </template>
  </div>
</template>
