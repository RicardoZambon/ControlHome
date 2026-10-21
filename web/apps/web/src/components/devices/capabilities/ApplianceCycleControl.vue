<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { Hand, Pause, Play, Square, Timer } from '@lucide/vue'
import { computed, type Component } from 'vue'
import type { Tone } from '../deviceState'
import { TONES } from '../tone'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `applianceCycle` (washer, dryer, dishwasher): the machine's state, phase and time left, with Start / Pause / Stop.
 * The commands only work while remote control is enabled on the appliance itself (the server refuses them otherwise),
 * so the buttons explain that instead of failing.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const machineState = computed(() => control.state.string('machineState') ?? 'idle')
const phase = computed(() => control.state.string('phase'))
const remaining = computed(() => control.state.number('remainingMinutes'))
const remote = computed(() => control.state.bool('remoteControlEnabled') === true)
const allowed = computed(() => control.enabled.value && remote.value)
const tone = computed<Tone>(() => {
  switch (machineState.value) {
    case 'running':
      return 'on'
    case 'paused':
      return 'warn'
    case 'finished':
      return 'info'
    case 'error':
      return 'danger'
    default:
      return 'off'
  }
})
const running = computed(() => machineState.value === 'running')

/** The tile's quick action: pause while running, else start. */
const quick = computed<{ command: string; icon: Component }>(() =>
  running.value ? { command: 'pause', icon: Pause } : { command: 'start', icon: Play },
)
const busy = computed(() => control.pending())
</script>

<template>
  <button
    v-if="variant === 'tile' && remote"
    type="button"
    :aria-label="`${control.commandLabel(quick.command)} · ${control.name.value}`"
    :aria-busy="busy || undefined"
    :disabled="!allowed || busy"
    :class="
      cn(
        'relative inline-flex size-11 shrink-0 touch-manipulation items-center justify-center rounded-full border bg-card text-foreground shadow-xs transition-[background-color,transform] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 motion-safe:active:scale-95 [&_svg]:size-5',
        running && 'border-transparent bg-state-on text-state-on-foreground hover:bg-state-on/95',
      )
    "
    @click="control.send(quick.command)"
  >
    <component :is="quick.icon" aria-hidden="true" />
    <span
      v-if="busy"
      aria-hidden="true"
      class="pointer-events-none absolute -inset-1 rounded-full border-2 border-transparent border-t-primary motion-safe:animate-spin"
    />
  </button>

  <div v-else-if="variant !== 'tile'" class="grid gap-6" data-slot="appliance-control">
    <div class="flex flex-wrap items-center gap-5">
      <span
        aria-hidden="true"
        :class="
          cn(
            'inline-flex size-16 items-center justify-center rounded-2xl [&_svg]:size-8',
            TONES[tone].bubble,
            TONES[tone].icon,
          )
        "
      >
        <Timer />
      </span>
      <div class="grid min-w-0 flex-1 gap-0.5">
        <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite">
          {{ control.valueLabel('machineState', machineState) }}
        </p>
        <p v-if="phase" class="text-sm text-muted-foreground">{{ phase }}</p>
      </div>
      <p
        v-if="remaining !== undefined && (machineState === 'running' || machineState === 'paused')"
        class="grid text-right"
      >
        <span class="font-serif text-4xl leading-none font-medium tabular-nums">{{ remaining }}</span>
        <span class="text-sm text-muted-foreground">{{ t('device.appliance.minutesLeft') }}</span>
      </p>
    </div>

    <template v-if="control.canControl.value">
      <div class="grid grid-cols-3 gap-2">
        <Button
          :disabled="!allowed || running"
          :loading="control.pending('start')"
          @click="control.send('start')"
        >
          <Play aria-hidden="true" />
          {{ control.commandLabel('start') }}
        </Button>
        <Button
          variant="outline"
          :disabled="!allowed || !running"
          :loading="control.pending('pause')"
          @click="control.send('pause')"
        >
          <Pause aria-hidden="true" />
          {{ control.commandLabel('pause') }}
        </Button>
        <Button
          variant="outline"
          :disabled="!allowed || machineState === 'idle'"
          :loading="control.pending('stop')"
          @click="control.send('stop')"
        >
          <Square aria-hidden="true" />
          {{ control.commandLabel('stop') }}
        </Button>
      </div>
      <p v-if="!remote" class="flex items-start gap-2 text-sm text-muted-foreground">
        <Hand class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        {{ t('device.appliance.remoteOff') }}
      </p>
    </template>
  </div>
</template>
