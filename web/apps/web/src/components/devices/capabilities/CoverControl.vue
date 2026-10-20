<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { ArrowDown, ArrowUp, KeyRound, Square } from '@lucide/vue'
import { computed, ref, type Component } from 'vue'
import FillSlider from '../controls/FillSlider.vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `cover` (blinds, curtains, gates, garage doors): a window whose blind follows the position, the position as a
 * slider, and Open / Stop / Close. On a gate or garage, opening (also part-way) is safety-critical: the PIN dialog
 * opens first. Position 100 is fully open.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const SLATS = 12
const isGate = computed(() => props.device.deviceClass === 'gate' || props.device.deviceClass === 'garage')
const position = computed(() => control.state.number('position'))
const coverState = computed(() => control.state.string('coverState'))
const moving = computed(() => coverState.value === 'opening' || coverState.value === 'closing')
const isOpen = computed(
  () => coverState.value === 'open' || (coverState.value !== 'closed' && (position.value ?? 0) > 0),
)
const canPosition = computed(() => control.hasCommand('setPosition') && control.state.has('position'))

const dragged = ref<number>()
const shown = computed(() => dragged.value ?? position.value ?? (coverState.value === 'open' ? 100 : 0))

const stateText = computed(() => {
  if (moving.value) return control.valueLabel('coverState', coverState.value!)
  const value = position.value
  if (value !== undefined && value > 0 && value < 100)
    return t('device.cover.percentOpen', { value: control.measure(value, '%') })
  if (coverState.value) return control.valueLabel('coverState', coverState.value)
  if (value !== undefined) return control.valueLabel('coverState', value > 0 ? 'open' : 'closed')
  return t('device.state.unknown')
})

/** The tile's quick action: stop while moving, else the other end. */
const quick = computed<{ command: string; icon: Component }>(() =>
  moving.value
    ? { command: 'stop', icon: Square }
    : isOpen.value
      ? { command: 'close', icon: ArrowDown }
      : { command: 'open', icon: ArrowUp },
)
const busy = computed(() => control.pending())

function setPosition(value: number): void {
  dragged.value = undefined
  void control.send('setPosition', { position: Math.round(value) })
}
</script>

<template>
  <button
    v-if="variant === 'tile'"
    type="button"
    :aria-label="`${control.commandLabel(quick.command)} · ${control.name.value}`"
    :aria-busy="busy || undefined"
    :disabled="!control.enabled.value || (busy && quick.command !== 'stop')"
    :class="
      cn(
        'relative inline-flex size-11 shrink-0 touch-manipulation items-center justify-center rounded-full border bg-card text-foreground shadow-xs transition-[background-color,transform] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 motion-safe:active:scale-95 [&_svg]:size-5',
        isOpen && !moving && 'border-transparent bg-state-on text-state-on-foreground hover:bg-state-on/95',
      )
    "
    @click="control.send(quick.command)"
  >
    <component :is="quick.icon" aria-hidden="true" :stroke-width="2.25" />
    <span
      v-if="busy"
      aria-hidden="true"
      class="pointer-events-none absolute -inset-1 rounded-full border-2 border-transparent border-t-primary motion-safe:animate-spin"
    />
  </button>

  <div v-else class="grid items-center gap-8 sm:grid-cols-[auto_minmax(0,1fr)]" data-slot="cover-control">
    <!-- The window (or the gate) and its blind -->
    <div
      aria-hidden="true"
      :class="
        cn(
          'relative mx-auto overflow-hidden border-4 border-border bg-accent/50 shadow-inner',
          isGate ? 'h-36 w-44 rounded-t-2xl border-b-0' : 'h-44 w-32 rounded-xl',
        )
      "
    >
      <div v-if="!isGate" class="absolute inset-y-0 left-1/2 w-1 -translate-x-1/2 bg-border" />
      <div
        class="absolute inset-x-0 top-0 flex flex-col gap-0.5 overflow-hidden bg-card/40 transition-[height] duration-700 ease-out"
        :style="{ height: `${100 - shown}%` }"
      >
        <span
          v-for="slat in SLATS"
          :key="slat"
          :class="cn('w-full shrink-0 rounded-xs bg-muted-foreground/35', isGate ? 'h-3.5' : 'h-2.5')"
        />
      </div>
      <div
        class="absolute inset-x-0 h-1.5 bg-muted-foreground/70 transition-[top] duration-700 ease-out"
        :style="{ top: `calc(${100 - shown}% - 0.375rem)` }"
      />
      <ArrowUp
        v-if="coverState === 'opening'"
        class="absolute right-1.5 bottom-1.5 size-4 text-primary motion-safe:animate-bounce"
      />
      <ArrowDown
        v-else-if="coverState === 'closing'"
        class="absolute right-1.5 bottom-1.5 size-4 text-primary motion-safe:animate-bounce"
      />
    </div>

    <div class="grid gap-5">
      <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite" data-slot="cover-state">
        {{ stateText }}
      </p>
      <div v-if="canPosition" class="grid gap-2">
        <span class="flex items-center justify-between text-sm text-muted-foreground">
          {{ control.attributeLabel('position') }}
          <span class="font-medium text-foreground tabular-nums">{{ control.measure(shown, '%') }}</span>
        </span>
        <FillSlider
          :model-value="position ?? 0"
          :label="t('device.cover.position', { name: control.name.value })"
          :value-text="control.measure(shown, '%')"
          :disabled="!control.enabled.value"
          @update:model-value="(value) => (dragged = value)"
          @commit="setPosition"
        />
      </div>
      <div v-if="control.canControl.value" class="grid grid-cols-3 gap-2">
        <Button
          variant="outline"
          :disabled="!control.enabled.value"
          :loading="control.pending('open')"
          @click="control.send('open')"
        >
          <KeyRound v-if="isGate" aria-hidden="true" />
          <ArrowUp v-else aria-hidden="true" />
          {{ control.commandLabel('open') }}
        </Button>
        <Button
          variant="outline"
          :disabled="!control.enabled.value"
          :loading="control.pending('stop')"
          @click="control.send('stop')"
        >
          <Square aria-hidden="true" />
          {{ control.commandLabel('stop') }}
        </Button>
        <Button
          variant="outline"
          :disabled="!control.enabled.value"
          :loading="control.pending('close')"
          @click="control.send('close')"
        >
          <ArrowDown aria-hidden="true" />
          {{ control.commandLabel('close') }}
        </Button>
      </div>
      <p v-if="isGate && control.canControl.value" class="text-xs text-muted-foreground">
        {{ t('device.cover.pinHint') }}
      </p>
    </div>
  </div>
</template>
