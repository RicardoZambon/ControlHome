<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { Check } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { SETUP_STEPS, type SetupStepId } from '../setup'

/** Progress through the wizard. Completed steps can be revisited. */
const props = defineProps<{ current: SetupStepId; reached: number }>()
const emit = defineEmits<{ go: [step: SetupStepId] }>()
const { t } = useI18n()

const indexOf = (step: SetupStepId) => SETUP_STEPS.indexOf(step)
</script>

<template>
  <nav :aria-label="t('setup.progress')">
    <p class="mb-3 text-sm text-muted-foreground sm:sr-only">
      {{ t('setup.stepOf', { current: indexOf(props.current) + 1, total: SETUP_STEPS.length }) }}
    </p>
    <ol class="flex items-center gap-2">
      <li
        v-for="(step, index) in SETUP_STEPS"
        :key="step"
        class="flex min-w-0 items-center gap-2"
        :class="index < SETUP_STEPS.length - 1 ? 'flex-1' : ''"
      >
        <button
          type="button"
          :disabled="index > props.reached || step === props.current"
          :aria-current="step === props.current ? 'step' : undefined"
          class="group flex min-w-0 items-center gap-2.5 rounded-full text-left outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-default"
          @click="emit('go', step)"
        >
          <span
            :class="
              cn(
                'flex size-8 shrink-0 items-center justify-center rounded-full border text-sm font-semibold transition-colors',
                step === props.current && 'border-primary bg-primary text-primary-foreground shadow-sm',
                index < indexOf(props.current) &&
                  'border-transparent bg-accent text-accent-foreground group-enabled:group-hover:bg-primary group-enabled:group-hover:text-primary-foreground',
                index > indexOf(props.current) && 'border-border bg-background text-muted-foreground',
              )
            "
          >
            <Check v-if="index < indexOf(props.current)" class="size-4" aria-hidden="true" />
            <span v-else aria-hidden="true">{{ index + 1 }}</span>
          </span>
          <span
            :class="
              cn(
                'hidden truncate text-sm sm:block',
                step === props.current ? 'font-medium text-foreground' : 'text-muted-foreground',
              )
            "
          >
            {{ t(`setup.steps.${step}.label`) }}
          </span>
          <span class="sr-only sm:hidden">{{ t(`setup.steps.${step}.label`) }}</span>
          <span v-if="index < indexOf(props.current)" class="sr-only">({{ t('setup.completed') }})</span>
        </button>
        <span
          v-if="index < SETUP_STEPS.length - 1"
          aria-hidden="true"
          :class="cn('h-px min-w-3 flex-1', index < indexOf(props.current) ? 'bg-primary/50' : 'bg-border')"
        />
      </li>
    </ol>
  </nav>
</template>
