<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { Check } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { SETUP_STEPS, type SetupStepId } from '../setup'

/**
 * Progress through the wizard. Completed steps can be revisited. Phones get a segmented bar with "Step 1 of 4 ·
 * Setup code"; wider screens numbered steps with their names (upcoming ones muted and inert).
 */
const props = defineProps<{ current: SetupStepId; reached: number }>()
const emit = defineEmits<{ go: [step: SetupStepId] }>()
const { t } = useI18n()

const indexOf = (step: SetupStepId) => SETUP_STEPS.indexOf(step)
const currentIndex = computed(() => indexOf(props.current))
</script>

<template>
  <nav :aria-label="t('setup.progress')">
    <!-- Phones: where you are, in words, over a thin segmented bar. -->
    <div class="grid gap-2.5 sm:hidden">
      <p class="text-sm">
        <span class="text-muted-foreground">{{
          t('setup.stepOf', { current: currentIndex + 1, total: SETUP_STEPS.length })
        }}</span>
        <span class="text-muted-foreground"> · </span>
        <span class="font-medium">{{ t(`setup.steps.${props.current}.label`) }}</span>
      </p>
      <div class="grid grid-cols-4 gap-1.5">
        <span
          v-for="(step, index) in SETUP_STEPS"
          :key="step"
          aria-hidden="true"
          :class="cn('h-1.5 rounded-full', index <= currentIndex ? 'bg-primary' : 'bg-border')"
        />
      </div>
    </div>

    <ol class="hidden items-center gap-2 sm:flex">
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
                'flex size-8 shrink-0 items-center justify-center rounded-full text-sm font-semibold transition-colors',
                step === props.current && 'bg-primary text-primary-foreground shadow-sm',
                index < currentIndex &&
                  'bg-accent text-accent-foreground group-enabled:group-hover:bg-primary group-enabled:group-hover:text-primary-foreground',
                index > currentIndex && 'bg-muted text-muted-foreground',
              )
            "
          >
            <Check v-if="index < currentIndex" class="size-4" aria-hidden="true" />
            <span v-else aria-hidden="true">{{ index + 1 }}</span>
          </span>
          <span
            :class="
              cn(
                'truncate text-sm',
                step === props.current ? 'font-medium text-foreground' : 'text-muted-foreground',
              )
            "
          >
            {{ t(`setup.steps.${step}.label`) }}
          </span>
          <span v-if="index < currentIndex" class="sr-only">({{ t('setup.completed') }})</span>
        </button>
        <span
          v-if="index < SETUP_STEPS.length - 1"
          aria-hidden="true"
          :class="cn('h-px min-w-3 flex-1', index < currentIndex ? 'bg-primary/50' : 'bg-border')"
        />
      </li>
    </ol>
  </nav>
</template>
