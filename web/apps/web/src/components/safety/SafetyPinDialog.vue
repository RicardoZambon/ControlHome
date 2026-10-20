<script setup lang="ts">
import { stepUp } from '@controlhome/api-client'
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  cn,
} from '@controlhome/ui'
import { Delete, KeyRound, ShieldAlert } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useProblem } from '../../composables/useProblem'
import { unwrap } from '../../lib/problem'
import { useSafetyPromptStore } from './safetyPrompt'

/**
 * The safety-PIN step-up (docs/architecture.md §8), opened by `useSafetyPromptStore().requestStepUp()` when a
 * safety-critical command (unlock, open the gate) needs it. A PIN pad with large keys (the keyboard's digits,
 * Backspace and Enter work too) and dots for the digits typed; `POST /api/auth/step-up` grants one command within 60 s.
 * A wrong PIN shakes the dots and clears them; too many lock the pad for a while; without a PIN it points to the profile.
 */
const PIN_MIN = 4
const PIN_MAX = 8
const KEYS = ['1', '2', '3', '4', '5', '6', '7', '8', '9'] as const

const { t } = useI18n()
const problem = useProblem()
const store = useSafetyPromptStore()

const open = computed(() => store.prompt !== null)
const pin = ref('')
const error = ref('')
const busy = ref(false)
const locked = ref(false)
const notSet = ref(false)
const shaking = ref(false)

watch(open, (value) => {
  if (!value) return
  pin.value = ''
  error.value = ''
  locked.value = false
  notSet.value = false
})

const canType = computed(() => !busy.value && !locked.value && !notSet.value)
const dots = computed(() => Math.max(PIN_MIN, pin.value.length))

function press(digit: string): void {
  if (!canType.value || pin.value.length >= PIN_MAX) return
  pin.value += digit
  error.value = ''
}

function erase(): void {
  if (!canType.value) return
  pin.value = pin.value.slice(0, -1)
}

function shake(): void {
  shaking.value = false
  requestAnimationFrame(() => (shaking.value = true))
}

async function submit(): Promise<void> {
  if (!canType.value || pin.value.length < PIN_MIN) return
  busy.value = true
  error.value = ''
  try {
    unwrap(await stepUp({ body: { pin: pin.value } }))
    store.complete(true)
  } catch (failure) {
    const code = problem.codeOf(failure)
    pin.value = ''
    if (code === 'safety.pin_not_set') notSet.value = true
    else {
      error.value = problem.message(failure)
      if (code === 'safety.locked') locked.value = true
      else shake()
    }
  } finally {
    busy.value = false
  }
}

function onKeydown(event: KeyboardEvent): void {
  if (event.ctrlKey || event.metaKey || event.altKey) return
  if (/^\d$/.test(event.key)) {
    event.preventDefault()
    press(event.key)
  } else if (event.key === 'Backspace') {
    event.preventDefault()
    erase()
  } else if (event.key === 'Enter') {
    event.preventDefault()
    void submit()
  }
}

function onOpenChange(value: boolean): void {
  if (!value && !busy.value) store.complete(false)
}
</script>

<template>
  <Dialog :open="open" @update:open="onOpenChange">
    <DialogContent
      :close-label="t('common.close')"
      class="gap-6 sm:max-w-sm"
      data-slot="safety-pin-dialog"
      @keydown="onKeydown"
    >
      <DialogHeader class="items-center text-center sm:text-center">
        <span
          aria-hidden="true"
          class="mb-1 inline-flex size-12 items-center justify-center rounded-2xl bg-accent text-accent-foreground [&_svg]:size-6"
        >
          <KeyRound />
        </span>
        <DialogTitle class="font-serif text-2xl font-medium tracking-tight">{{
          t('safety.dialog.title')
        }}</DialogTitle>
        <DialogDescription>
          {{
            store.prompt?.action
              ? t('safety.dialog.for', { action: store.prompt.action })
              : t('safety.dialog.description')
          }}
        </DialogDescription>
      </DialogHeader>

      <!-- No PIN yet: where to set one -->
      <div v-if="notSet" class="grid gap-4 text-center" role="alert">
        <p class="flex items-start gap-2 text-left text-sm">
          <ShieldAlert class="mt-0.5 size-4 shrink-0 text-state-warn" aria-hidden="true" />
          {{ t('errors.safety.pin_not_set') }}
        </p>
        <Button as-child>
          <RouterLink :to="{ name: 'profile', hash: '#safety-pin' }" @click="store.complete(false)">
            {{ t('safety.dialog.setPin') }}
          </RouterLink>
        </Button>
      </div>

      <template v-else>
        <!-- The digits typed, as dots -->
        <div class="grid justify-items-center gap-3">
          <div
            :class="
              cn('flex items-center gap-3', shaking && 'motion-safe:animate-[ch-shake_0.4s_ease-in-out]')
            "
            aria-hidden="true"
            data-slot="pin-dots"
            @animationend="shaking = false"
          >
            <span
              v-for="index in dots"
              :key="index"
              :class="
                cn(
                  'size-3.5 rounded-full border-2 transition-colors duration-150',
                  index <= pin.length ? 'border-primary bg-primary' : 'border-input bg-transparent',
                  error && index > pin.length && 'border-destructive/60',
                )
              "
            />
          </div>
          <p class="sr-only" role="status" aria-live="polite">
            {{ t('safety.dialog.entered', { count: pin.length, max: PIN_MAX }) }}
          </p>
          <p v-if="error" class="text-center text-sm text-destructive" role="alert" data-slot="pin-error">
            {{ error }}
          </p>
        </div>

        <!-- The pad -->
        <div class="grid grid-cols-3 gap-2.5" role="group" :aria-label="t('safety.dialog.keypad')">
          <Button
            v-for="digit in KEYS"
            :key="digit"
            type="button"
            variant="secondary"
            class="h-14 rounded-2xl text-xl font-medium tabular-nums"
            :disabled="!canType"
            @click="press(digit)"
          >
            {{ digit }}
          </Button>
          <span aria-hidden="true" />
          <Button
            type="button"
            variant="secondary"
            class="h-14 rounded-2xl text-xl font-medium tabular-nums"
            :disabled="!canType"
            @click="press('0')"
          >
            0
          </Button>
          <Button
            type="button"
            variant="ghost"
            class="h-14 rounded-2xl [&_svg]:size-5"
            :disabled="!canType || pin.length === 0"
            :aria-label="t('safety.dialog.erase')"
            @click="erase"
          >
            <Delete aria-hidden="true" />
          </Button>
        </div>
      </template>

      <DialogFooter class="grid grid-cols-2 gap-2 sm:flex">
        <Button variant="outline" :disabled="busy" @click="store.complete(false)">{{
          t('common.cancel')
        }}</Button>
        <Button
          v-if="!notSet"
          :disabled="locked || pin.length < PIN_MIN"
          :loading="busy"
          data-slot="pin-confirm"
          @click="submit"
        >
          {{ t('safety.dialog.confirm') }}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
