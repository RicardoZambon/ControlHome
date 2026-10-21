<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { DoorOpen, KeyRound } from '@lucide/vue'
import { computed, onBeforeUnmount, ref } from 'vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `doorOpener`: one safety-critical action (the electric strike, a buzzer). It has no state of its own: after the
 * device acknowledges, "Opened" shows for a few seconds.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const pending = computed(() => control.pending('open'))
const opened = ref(false)
let openedTimer: ReturnType<typeof setTimeout> | undefined

async function open(): Promise<void> {
  const result = await control.send('open')
  if (result !== 'confirmed') return
  opened.value = true
  clearTimeout(openedTimer)
  openedTimer = setTimeout(() => (opened.value = false), 6_000)
}

onBeforeUnmount(() => clearTimeout(openedTimer))
</script>

<template>
  <!-- Tiles: the action and, under it, what it does ("Open") -->
  <span v-if="variant === 'tile'" class="flex flex-col items-center gap-1">
    <button
      type="button"
      :aria-label="`${control.commandLabel('open')} · ${control.name.value}`"
      :aria-busy="pending || undefined"
      :disabled="!control.enabled.value || pending"
      :class="
        cn(
          'relative inline-flex size-11 shrink-0 touch-manipulation items-center justify-center rounded-full border bg-card text-foreground shadow-xs transition-[background-color,transform] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 motion-safe:active:scale-95 [&_svg]:size-5',
          opened && 'border-transparent bg-state-on text-state-on-foreground hover:bg-state-on',
        )
      "
      @click="open"
    >
      <DoorOpen aria-hidden="true" />
      <span
        v-if="pending"
        aria-hidden="true"
        class="pointer-events-none absolute -inset-1 rounded-full border-2 border-transparent border-t-primary motion-safe:animate-spin"
      />
    </button>
    <span
      class="text-xs leading-none font-medium text-muted-foreground"
      aria-hidden="true"
      data-slot="quick-verb"
    >
      {{ control.commandLabel('open') }}
    </span>
  </span>

  <div v-else class="flex flex-col items-center gap-5 py-2 text-center" data-slot="door-opener-control">
    <span
      aria-hidden="true"
      :class="
        cn(
          'inline-flex size-28 items-center justify-center rounded-full transition-colors duration-500 [&_svg]:size-12',
          opened ? 'bg-state-on/15 text-state-on' : 'bg-muted text-muted-foreground',
        )
      "
    >
      <DoorOpen :stroke-width="1.75" />
    </span>
    <p class="font-serif text-3xl font-medium tracking-tight" aria-live="polite">
      {{
        pending
          ? t('device.doorOpener.opening')
          : opened
            ? t('device.doorOpener.opened')
            : t('device.doorOpener.ready')
      }}
    </p>
    <template v-if="control.canControl.value">
      <Button
        size="lg"
        class="w-full max-w-xs"
        :disabled="!control.enabled.value"
        :loading="pending"
        @click="open"
      >
        <KeyRound aria-hidden="true" />
        {{ control.commandLabel('open') }}
      </Button>
      <p class="text-xs text-muted-foreground">{{ t('device.doorOpener.pinHint') }}</p>
    </template>
  </div>
</template>
