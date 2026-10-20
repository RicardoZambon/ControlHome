<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { KeyRound, Lock, LockOpen, ShieldAlert } from '@lucide/vue'
import { computed } from 'vue'
import type { Tone } from '../deviceState'
import { TONES } from '../tone'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `lock`: the lock's state as a large emblem with Lock / Unlock buttons. Unlocking is safety-critical: the server asks
 * for the safety PIN (the dialog opens) and the command goes out at most once, so both buttons wait while one is
 * pending. Locking (the safe direction) never needs the PIN. Viewers see the state only.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const lockState = computed(() => control.state.string('lockState') ?? 'unknown')
const tone = computed<Tone>(() =>
  lockState.value === 'locked'
    ? 'on'
    : lockState.value === 'unlocked'
      ? 'warn'
      : lockState.value === 'jammed'
        ? 'danger'
        : 'neutral',
)
const locking = computed(() => control.pending('lock'))
const unlocking = computed(() => control.pending('unlock'))
const busy = computed(() => locking.value || unlocking.value)
const icon = computed(() => (lockState.value === 'unlocked' ? LockOpen : Lock))
const stateText = computed(() =>
  locking.value
    ? t('device.lock.locking')
    : unlocking.value
      ? t('device.lock.unlocking')
      : control.valueLabel('lockState', lockState.value),
)

/** The tile's quick action: the other state. */
const quickCommand = computed(() => (lockState.value === 'locked' ? 'unlock' : 'lock'))
</script>

<template>
  <button
    v-if="variant === 'tile'"
    type="button"
    :aria-label="`${control.commandLabel(quickCommand)} · ${control.name.value}`"
    :aria-busy="busy || undefined"
    :disabled="!control.enabled.value || busy"
    data-slot="lock-quick"
    :data-state="lockState"
    :class="
      cn(
        'relative inline-flex size-11 shrink-0 touch-manipulation items-center justify-center rounded-full transition-[background-color,transform] duration-300 outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 motion-safe:active:scale-95 [&_svg]:size-5',
        tone === 'neutral' ? 'border border-dashed bg-card text-muted-foreground' : TONES[tone].solid,
      )
    "
    @click="control.send(quickCommand)"
  >
    <component :is="icon" aria-hidden="true" :stroke-width="2.25" />
    <span
      v-if="busy"
      aria-hidden="true"
      class="pointer-events-none absolute -inset-1 rounded-full border-2 border-transparent border-t-primary motion-safe:animate-spin"
    />
  </button>

  <div v-else class="flex flex-col items-center gap-5 py-2 text-center" data-slot="lock-control">
    <span
      aria-hidden="true"
      :class="
        cn(
          'relative inline-flex size-28 items-center justify-center rounded-full transition-colors duration-500 [&_svg]:size-12',
          TONES[tone].bubble,
          TONES[tone].icon,
        )
      "
    >
      <component :is="icon" :stroke-width="1.75" />
      <span
        v-if="busy"
        class="pointer-events-none absolute -inset-1.5 rounded-full border-2 border-transparent border-t-primary motion-safe:animate-spin"
      />
    </span>
    <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite" data-slot="lock-state">
      {{ stateText }}
    </p>
    <p
      v-if="lockState === 'jammed'"
      class="flex max-w-sm items-start gap-2 text-left text-sm text-muted-foreground"
      role="alert"
    >
      <ShieldAlert class="mt-0.5 size-4 shrink-0 text-state-danger" aria-hidden="true" />
      {{ t('device.lock.jammed') }}
    </p>

    <template v-if="control.canControl.value">
      <div class="grid w-full max-w-sm grid-cols-2 gap-3">
        <Button
          size="lg"
          :variant="lockState === 'locked' ? 'outline' : 'default'"
          :disabled="!control.enabled.value || unlocking"
          :loading="locking"
          @click="control.send('lock')"
        >
          <Lock aria-hidden="true" />
          {{ control.commandLabel('lock') }}
        </Button>
        <Button
          size="lg"
          :variant="lockState === 'locked' ? 'default' : 'outline'"
          :disabled="!control.enabled.value || locking"
          :loading="unlocking"
          @click="control.send('unlock')"
        >
          <KeyRound aria-hidden="true" />
          {{ control.commandLabel('unlock') }}
        </Button>
      </div>
      <p class="text-xs text-muted-foreground">{{ t('device.lock.pinHint') }}</p>
    </template>
  </div>
</template>
