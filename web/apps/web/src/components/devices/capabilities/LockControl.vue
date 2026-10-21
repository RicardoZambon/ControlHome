<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { KeyRound, Lock, LockOpen, ShieldAlert } from '@lucide/vue'
import { computed } from 'vue'
import type { Tone } from '../deviceState'
import { pendingLabel } from '../pendingLabel'
import { TONES } from '../tone'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `lock`. Buttons show the action they take, never the state (the state is the emblem and the words):
 * - locked: one quiet "Unlock…" (safety-critical: the safety PIN is asked first, and nothing happens until it is
 *   accepted — meanwhile the lock reads "Locked · Waiting for your PIN");
 * - unlocked: the amber emblem and one solid "Lock" (the safe direction, never gated);
 * - jammed: the danger emblem, "Try locking again" and a quiet "Unlock…";
 * - unknown: both.
 * Both buttons wait while a command is pending (at most once). Viewers see the state only. On tiles the quick action
 * follows the same rule: a quiet outlined key to unlock, a solid amber lock to lock.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t, te, locale } = control

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
const pending = computed(() => control.pendingCommand())
const awaitingPin = computed(() => pending.value?.awaitingPin === true)
const locking = computed(() => pending.value?.command === 'lock')
const unlocking = computed(() => pending.value?.command === 'unlock' && !awaitingPin.value)
const busy = computed(() => pending.value !== undefined)
const emblem = computed(() =>
  lockState.value === 'unlocked' ? LockOpen : lockState.value === 'jammed' ? ShieldAlert : Lock,
)
const stateText = computed(() =>
  pending.value && !awaitingPin.value
    ? pendingLabel(pending.value, t, te, locale.value)
    : control.valueLabel('lockState', lockState.value),
)

const showLock = computed(() => lockState.value !== 'locked')
const showUnlock = computed(() => lockState.value !== 'unlocked')
const unlockAction = computed(() => t('device.lock.unlockAction'))

/** The tile's quick action: the other state (unknown and jammed: lock, the safe direction). */
const quickCommand = computed(() => (lockState.value === 'locked' ? 'unlock' : 'lock'))
const quickLabel = computed(() =>
  quickCommand.value === 'unlock' ? unlockAction.value : control.commandLabel('lock'),
)
const quickClass = computed(() => {
  if (quickCommand.value === 'unlock')
    return 'border-2 border-muted-foreground/60 bg-transparent text-foreground hover:border-foreground/70 hover:bg-accent'
  if (lockState.value === 'unlocked') return cn(TONES.warn.solid, 'shadow-md shadow-state-warn/30')
  if (lockState.value === 'jammed') return 'border-2 border-state-danger bg-transparent text-state-danger'
  return 'border-2 border-dashed border-muted-foreground/60 bg-transparent text-foreground'
})
</script>

<template>
  <button
    v-if="variant === 'tile'"
    type="button"
    :aria-label="`${quickLabel} · ${control.name.value}`"
    :title="quickLabel"
    :aria-busy="busy || undefined"
    :disabled="!control.enabled.value || busy"
    data-slot="lock-quick"
    :data-state="lockState"
    :data-action="quickCommand"
    :class="
      cn(
        'relative inline-flex size-11 shrink-0 touch-manipulation items-center justify-center rounded-full transition-[background-color,border-color,transform] duration-300 outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 motion-safe:active:scale-95 [&_svg]:size-5',
        quickClass,
      )
    "
    @click="control.send(quickCommand)"
  >
    <KeyRound v-if="quickCommand === 'unlock'" aria-hidden="true" :stroke-width="2" />
    <Lock v-else aria-hidden="true" :stroke-width="2.25" />
    <span
      v-if="busy"
      aria-hidden="true"
      class="pointer-events-none absolute -inset-1 rounded-full border-2 border-transparent border-t-foreground/70 motion-safe:animate-spin"
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
          lockState === 'unlocked' && 'ring-2 ring-state-warn/50',
        )
      "
    >
      <component :is="emblem" :stroke-width="1.75" />
      <span
        v-if="busy && !awaitingPin"
        class="pointer-events-none absolute -inset-1.5 rounded-full border-2 border-transparent border-t-foreground/60 motion-safe:animate-spin"
      />
    </span>
    <div class="grid gap-1">
      <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite" data-slot="lock-state">
        {{ stateText }}
      </p>
      <p v-if="awaitingPin" class="text-sm text-muted-foreground" data-slot="lock-awaiting-pin">
        {{ t('device.pending.awaitingPin') }}
      </p>
    </div>
    <p
      v-if="lockState === 'jammed'"
      class="flex max-w-sm items-start gap-2 text-left text-sm text-muted-foreground"
      role="alert"
    >
      <ShieldAlert class="mt-0.5 size-4 shrink-0 text-state-danger" aria-hidden="true" />
      {{ t('device.lock.jammed') }}
    </p>

    <template v-if="control.canControl.value">
      <div :class="cn('grid w-full max-w-sm gap-3', showLock && showUnlock && 'grid-cols-2')">
        <Button
          v-if="showLock"
          size="lg"
          :disabled="!control.enabled.value || (busy && !locking)"
          :loading="locking"
          data-slot="lock-action"
          @click="control.send('lock')"
        >
          <Lock aria-hidden="true" />
          {{ lockState === 'jammed' ? t('device.lock.tryAgain') : control.commandLabel('lock') }}
        </Button>
        <Button
          v-if="showUnlock"
          size="lg"
          variant="outline"
          :disabled="!control.enabled.value || (busy && !unlocking)"
          :loading="unlocking"
          data-slot="unlock-action"
          @click="control.send('unlock')"
        >
          <KeyRound aria-hidden="true" />
          {{ unlockAction }}
        </Button>
      </div>
      <p v-if="showUnlock" class="text-xs text-muted-foreground">{{ t('device.lock.pinHint') }}</p>
    </template>
  </div>
</template>
