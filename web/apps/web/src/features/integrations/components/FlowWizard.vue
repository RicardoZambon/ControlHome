<script setup lang="ts">
import type { ConnectorDto, FlowStepDto } from '@controlhome/api-client'
import { Button, Label, Progress, RadioGroup, RadioGroupItem, cn } from '@controlhome/ui'
import { CircleCheck, ExternalLink, OctagonAlert, RefreshCw } from '@lucide/vue'
import { useNow } from '@vueuse/core'
import { computed, onMounted, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import SchemaField from '../../../components/schema/SchemaField.vue'
import SchemaForm from '../../../components/schema/SchemaForm.vue'
import { humanize, localized, type SchemaFieldDefinition } from '../../../components/schema/schema'
import { useProblem } from '../../../composables/useProblem'
import {
  chooseInput,
  formErrors,
  formFields,
  formValues,
  orphanErrors,
  pasteBackInput,
  safeExternalUrl,
  secondsLeft,
  stepType,
  useIntegrationFlow,
} from '../flow'

/**
 * One setup or re-authorization flow, step by step: a form (the shared schema form, with the server's field errors), an
 * external page to open (and paste a code back from), a wait for something on the device (polled, with a countdown),
 * a choice, then done or aborted. Cancel abandons the flow on the server; leaving the page does too.
 */
const props = defineProps<{
  /** Starts the flow. */
  start: () => Promise<FlowStepDto>
  connector?: ConnectorDto
  /**
   * The line above each step's title. A page that shows its own steps (Connect → Review devices) names the one this
   * flow is ("Connect"), so the two never count differently; otherwise the flow's own "Step 2".
   */
  eyebrow?: string
}>()

const emit = defineEmits<{
  /** The flow created (or updated) this integration. */
  done: [integrationId: string]
  cancel: []
}>()

const { t, te, locale } = useI18n()
const problem = useProblem()
const id = useId()
const flow = useIntegrationFlow({ start: () => props.start() })
const now = useNow({ interval: 500 })

onMounted(() => void flow.begin())

const step = computed(() => flow.step.value)
const type = computed(() => (step.value ? stepType(step.value) : null))
const title = computed(() => localized(step.value?.title, locale.value))
const description = computed(() => localized(step.value?.description, locale.value))

// --- Form ------------------------------------------------------------------------------------------------------------
const values = ref<Record<string, unknown>>({})
const fields = computed<SchemaFieldDefinition[]>(() =>
  step.value ? formFields(step.value, locale.value) : [],
)
const errors = computed(() => (step.value ? formErrors(step.value, locale.value) : {}))
const otherErrors = computed(() => (step.value ? orphanErrors(step.value, locale.value) : []))

// A new step starts from its own values (the server sends the typed values back with a rejected form).
watch(
  () => step.value,
  (next, previous) => {
    if (!next) return
    if (next.flowId !== previous?.flowId || next.stepId !== previous?.stepId || stepType(next) === 'form')
      values.value = formValues(next)
    pasted.value = ''
    choice.value = next.choices[0]?.key ?? ''
    if (stepType(next) === 'done' && next.integrationId) emit('done', next.integrationId)
  },
)

function optionLabel(field: SchemaFieldDefinition, value: string): string {
  // Option values may not be message keys as they are ("pt-BR"): letters and digits only.
  const key = `integrations.flow.options.${field.key}.${value.replace(/[^\p{L}\p{N}]/gu, '')}`
  return te(key) ? t(key) : humanize(value)
}

// --- External page ---------------------------------------------------------------------------------------------------
const pasted = ref<unknown>('')
const externalUrl = computed(() => safeExternalUrl(step.value?.url))
const externalHost = computed(() => (externalUrl.value ? new URL(externalUrl.value).host : ''))
const pasteField = computed<SchemaFieldDefinition | null>(() => {
  const field = step.value?.pasteBack
  if (!field) return null
  return {
    key: field.key,
    label: localized(field.label, locale.value),
    schema: field.schema,
    required: field.required,
    secret: field.secret,
    help: field.help ? localized(field.help, locale.value) : undefined,
  }
})

// --- Progress ----------------------------------------------------------------------------------------------------------
const left = computed(() => secondsLeft(flow.progressEndsAt.value, now.value.getTime()))
const total = ref<number | null>(null)
watch(
  () => flow.progressEndsAt.value,
  (endsAt) => (total.value = endsAt === null ? null : Math.max(1, secondsLeft(endsAt, Date.now()) ?? 1)),
)
// `now` ticks every half second, so right after a step changes it can lag behind the moment `total` was taken.
const progressValue = computed(() =>
  left.value === null || !total.value
    ? 0
    : Math.min(100, Math.max(0, Math.round((left.value / total.value) * 100))),
)

// --- Choose ------------------------------------------------------------------------------------------------------------
const choice = ref('')

// --- Answers -----------------------------------------------------------------------------------------------------------
const requestError = computed(() => (flow.error.value ? problem.message(flow.error.value) : ''))
const expired = computed(() => problem.codeOf(flow.error.value) === 'flows.not_found')

async function cancel(): Promise<void> {
  await flow.cancel()
  emit('cancel')
}

function restart(): void {
  void flow.begin()
}

const connectorName = computed(() =>
  props.connector ? localized(props.connector.displayName, locale.value) : '',
)
const abortMessage = computed(
  () => localized(step.value?.message, locale.value) || t('integrations.flow.aborted'),
)
</script>

<template>
  <section
    class="grid gap-6"
    data-slot="flow-wizard"
    :data-step="type ?? 'loading'"
    :aria-busy="flow.busy.value"
  >
    <!-- Starting -->
    <div
      v-if="!step && !flow.error.value"
      class="grid justify-items-center gap-4 py-12 text-center"
      role="status"
    >
      <span class="relative flex size-12" aria-hidden="true">
        <span class="absolute inset-0 rounded-full bg-primary/20 motion-safe:animate-ping" />
        <span class="relative size-12 rounded-full border-2 border-primary/40 bg-card" />
      </span>
      <p class="text-muted-foreground">{{ t('integrations.flow.starting', { name: connectorName }) }}</p>
    </div>

    <!-- Could not start, or the flow expired -->
    <div v-else-if="!step" class="grid gap-4">
      <FormAlert :message="requestError" />
      <div class="flex flex-wrap gap-2">
        <Button @click="restart">
          <RefreshCw />
          {{ t('integrations.flow.restart') }}
        </Button>
        <Button variant="outline" @click="emit('cancel')">{{ t('common.cancel') }}</Button>
      </div>
    </div>

    <template v-else>
      <header v-if="type !== 'done' && type !== 'abort'" class="grid gap-2">
        <p class="eyebrow">{{ eyebrow ?? t('integrations.flow.step', { n: flow.count.value }) }}</p>
        <h2 class="font-serif text-2xl leading-tight font-medium tracking-tight text-balance sm:text-3xl">
          {{ title || connectorName }}
        </h2>
        <p v-if="description" class="max-w-prose text-pretty text-muted-foreground">{{ description }}</p>
      </header>

      <FormAlert v-if="requestError && type !== 'progress'" :message="requestError" />
      <div v-if="expired" class="-mt-2">
        <Button variant="outline" size="sm" @click="restart">
          <RefreshCw />
          {{ t('integrations.flow.restart') }}
        </Button>
      </div>

      <!-- Form -->
      <template v-if="type === 'form'">
        <FormAlert v-for="message in otherErrors" :key="message" :message="message" />
        <SchemaForm
          v-model="values"
          :fields="fields"
          :errors="errors"
          :busy="flow.busy.value"
          :option-label="optionLabel"
          @submit="(input) => flow.answer(input)"
        >
          <template #actions="{ busy }">
            <div class="flex flex-wrap justify-end gap-2 pt-2">
              <Button type="button" variant="ghost" :disabled="busy" @click="cancel">
                {{ t('common.cancel') }}
              </Button>
              <Button type="submit" :loading="busy" data-slot="flow-continue">{{
                t('common.continue')
              }}</Button>
            </div>
          </template>
        </SchemaForm>
      </template>

      <!-- External page -->
      <form
        v-else-if="type === 'externalUrl'"
        novalidate
        class="grid gap-5"
        @submit.prevent="flow.answer(pasteBackInput(step, pasted))"
      >
        <div class="grid gap-3 rounded-2xl border bg-muted/40 p-5">
          <p class="text-sm text-muted-foreground">
            {{ t('integrations.flow.external', { host: externalHost }) }}
          </p>
          <Button v-if="externalUrl" as-child class="justify-self-start">
            <a :href="externalUrl" target="_blank" rel="noopener noreferrer">
              <ExternalLink />
              {{ t('integrations.flow.openPage', { host: externalHost }) }}
            </a>
          </Button>
          <p v-else class="text-sm text-destructive">{{ t('integrations.flow.unsafeUrl') }}</p>
        </div>
        <SchemaField
          v-if="pasteField"
          v-model="pasted"
          :schema="pasteField.schema"
          :label="pasteField.label"
          :help="pasteField.help"
          :required="pasteField.required"
          :secret="pasteField.secret"
          :disabled="flow.busy.value"
        />
        <div class="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="ghost" :disabled="flow.busy.value" @click="cancel">
            {{ t('common.cancel') }}
          </Button>
          <Button type="submit" :loading="flow.busy.value" data-slot="flow-continue">
            {{ pasteField ? t('common.continue') : t('integrations.flow.finishedThere') }}
          </Button>
        </div>
      </form>

      <!-- Waiting for something on the device -->
      <div v-else-if="type === 'progress'" class="grid gap-6" data-slot="flow-progress">
        <div class="grid place-items-center py-6" aria-hidden="true">
          <span class="relative flex size-28 items-center justify-center">
            <span class="absolute inset-0 rounded-full border border-primary/25 motion-safe:animate-ping" />
            <span class="absolute inset-3 rounded-full border border-primary/35" />
            <span class="absolute inset-7 rounded-full bg-primary/15" />
            <span class="relative size-6 rounded-full bg-primary shadow-md" />
          </span>
        </div>
        <div v-if="left !== null" class="grid gap-2">
          <div class="flex items-center justify-between text-sm">
            <span class="text-muted-foreground">{{ t('integrations.flow.waiting') }}</span>
            <span class="font-medium tabular-nums" aria-live="off">
              {{ t('integrations.flow.secondsLeft', { s: left }) }}
            </span>
          </div>
          <Progress
            :model-value="progressValue"
            :aria-label="t('integrations.flow.timeLeft')"
            class="h-1.5"
          />
        </div>
        <p v-if="requestError" class="text-sm text-muted-foreground" role="status">
          {{ t('integrations.flow.retrying') }}
        </p>
        <div class="flex justify-end">
          <Button variant="outline" @click="cancel">{{ t('common.cancel') }}</Button>
        </div>
      </div>

      <!-- A choice -->
      <form
        v-else-if="type === 'choose'"
        novalidate
        class="grid gap-5"
        @submit.prevent="flow.answer(chooseInput(choice))"
      >
        <RadioGroup v-model="choice" class="grid gap-2" :aria-label="title || t('integrations.flow.choose')">
          <Label
            v-for="option in step.choices"
            :key="option.key"
            :for="`${id}-${option.key}`"
            :class="
              cn(
                'flex cursor-pointer items-start gap-3 rounded-xl border bg-card p-4 font-normal transition-colors hover:border-ring/40',
                choice === option.key && 'border-primary bg-accent/50 ring-1 ring-primary',
              )
            "
          >
            <RadioGroupItem :id="`${id}-${option.key}`" :value="option.key" class="mt-0.5" />
            <span class="grid gap-0.5">
              <span class="font-medium">{{ localized(option.label, locale) }}</span>
              <span v-if="option.description" class="text-sm text-muted-foreground">
                {{ localized(option.description, locale) }}
              </span>
            </span>
          </Label>
        </RadioGroup>
        <div class="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="ghost" :disabled="flow.busy.value" @click="cancel">
            {{ t('common.cancel') }}
          </Button>
          <Button type="submit" :disabled="!choice" :loading="flow.busy.value" data-slot="flow-continue">
            {{ t('common.continue') }}
          </Button>
        </div>
      </form>

      <!-- Done: the page takes over (import review, or back to the integration) -->
      <div v-else-if="type === 'done'" class="grid justify-items-center gap-3 py-8 text-center" role="status">
        <span
          class="flex size-14 items-center justify-center rounded-full bg-state-on/15 text-state-on motion-safe:animate-in motion-safe:zoom-in-50"
          aria-hidden="true"
        >
          <CircleCheck class="size-7" />
        </span>
        <p class="font-serif text-2xl font-medium tracking-tight">{{ t('integrations.flow.done') }}</p>
      </div>

      <!-- Aborted -->
      <div v-else class="grid gap-5" role="alert" data-slot="flow-abort">
        <div class="flex items-start gap-4 rounded-2xl border border-destructive/40 bg-destructive/5 p-5">
          <OctagonAlert class="mt-0.5 size-6 shrink-0 text-destructive" aria-hidden="true" />
          <div class="grid gap-1">
            <p class="font-medium">{{ t('integrations.flow.abortedTitle') }}</p>
            <p class="text-sm text-muted-foreground">{{ abortMessage }}</p>
          </div>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button @click="restart">
            <RefreshCw />
            {{ t('integrations.flow.restart') }}
          </Button>
          <Button variant="outline" @click="emit('cancel')">{{ t('integrations.flow.back') }}</Button>
        </div>
      </div>
    </template>
  </section>
</template>
