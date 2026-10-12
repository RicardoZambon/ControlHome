<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { Check } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { SetupStepId } from '../setup'

/**
 * Progress through the wizard's `steps` (four, or five with the database step). Completed steps can be revisited. Phones
 * get a segmented bar with "Step 1 of 5 · Setup code"; wider screens numbered steps with their names (upcoming ones muted
 * and inert). Where the row is too narrow for every name (a container query: five names need 42rem, four 36rem), only
 * the current step keeps its name on screen; the others keep it for screen readers and as a tooltip, never truncated.
 */
const props = defineProps<{ steps: readonly SetupStepId[]; current: SetupStepId; reached: number }>()
const emit = defineEmits<{ go: [step: SetupStepId] }>()
const { t } = useI18n()

const currentIndex = computed(() => props.steps.indexOf(props.current))
const otherLabelClass = computed(() =>
  props.steps.length > 4 ? 'sr-only @2xl:not-sr-only' : 'sr-only @xl:not-sr-only',
)
</script>

<template>
  <nav :aria-label="t('setup.progress')" class="@container">
    <!-- Phones: where you are, in words, over a thin segmented bar. -->
    <div class="grid gap-2.5 sm:hidden">
      <p class="text-sm">
        <span class="text-muted-foreground">{{
          t('setup.stepOf', { current: currentIndex + 1, total: props.steps.length })
        }}</span>
        <span class="text-muted-foreground"> · </span>
        <span class="font-medium">{{ t(`setup.steps.${props.current}.label`) }}</span>
      </p>
      <div class="flex gap-1.5">
        <span
          v-for="(step, index) in props.steps"
          :key="step"
          aria-hidden="true"
          :class="cn('h-1.5 flex-1 rounded-full', index <= currentIndex ? 'bg-primary' : 'bg-border')"
        />
      </div>
    </div>

    <ol class="hidden items-center gap-2 sm:flex">
      <li
        v-for="(step, index) in props.steps"
        :key="step"
        class="flex min-w-0 items-center gap-2"
        :class="index < props.steps.length - 1 ? 'flex-auto' : ''"
      >
        <button
          type="button"
          :disabled="index > props.reached || step === props.current"
          :aria-current="step === props.current ? 'step' : undefined"
          :title="t(`setup.steps.${step}.label`)"
          class="group flex min-w-0 items-center gap-2 rounded-full text-left outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-default"
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
                'text-sm whitespace-nowrap',
                step === props.current
                  ? 'truncate font-medium text-foreground'
                  : cn('text-muted-foreground', otherLabelClass),
              )
            "
          >
            {{ t(`setup.steps.${step}.label`) }}
          </span>
          <span v-if="index < currentIndex" class="sr-only">({{ t('setup.completed') }})</span>
        </button>
        <span
          v-if="index < props.steps.length - 1"
          aria-hidden="true"
          :class="cn('h-px min-w-2 flex-1', index < currentIndex ? 'bg-primary/50' : 'bg-border')"
        />
      </li>
    </ol>
  </nav>
</template>
