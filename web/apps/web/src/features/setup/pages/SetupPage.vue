<script setup lang="ts">
import { Card, CardContent, toast } from '@controlhome/ui'
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useProblem } from '../../../composables/useProblem'
import { currentLocale } from '../../../i18n'
import { fieldErrorsOf } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'
import AdminStep from '../components/AdminStep.vue'
import CodeStep from '../components/CodeStep.vue'
import DatabaseStep from '../components/DatabaseStep.vue'
import DatabaseWaiting from '../components/DatabaseWaiting.vue'
import HomeStep from '../components/HomeStep.vue'
import ReviewStep from '../components/ReviewStep.vue'
import SetupStepper from '../components/SetupStepper.vue'
import {
  createDraft,
  errorsByStep,
  firstStepWithErrors,
  nextStep,
  previousStep,
  setupSteps,
  toRequest,
  type AdminDraft,
  type DatabaseDraft,
  type HomeDraft,
  type SetupStepId,
} from '../setup'

const { t } = useI18n()
const router = useRouter()
const session = useSessionStore()
const problem = useProblem()

// --- Database: none configured → a wizard step; configured (environment) but not ready yet → wait for it ----------
const status = computed(() => session.status)
/** Once the server reported no database connection, the wizard keeps its database step (shown as done afterwards). */
const includeDatabase = ref(false)
watch(
  () => status.value?.databaseConfigured,
  (configured) => {
    if (configured === false) includeDatabase.value = true
  },
  { immediate: true },
)
const steps = computed(() => setupSteps(includeDatabase.value))
const phase = computed<'waiting' | 'wizard'>(() => {
  if (includeDatabase.value || !status.value) return 'wizard'
  return status.value.databaseState === 'Ready' ? 'wizard' : 'waiting'
})
const checking = ref(false)

async function recheck(): Promise<void> {
  checking.value = true
  try {
    await session.loadStatus()
  } finally {
    checking.value = false
  }
}

// While a configured database is connecting or migrating, follow its progress.
let poll: ReturnType<typeof setInterval> | undefined
watch(
  () => phase.value === 'waiting' && status.value?.databaseState !== 'Failed',
  (waiting) => {
    if (poll) clearInterval(poll)
    poll = waiting ? setInterval(() => void session.loadStatus(), 3000) : undefined
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  if (poll) clearInterval(poll)
})

// --- Wizard ----------------------------------------------------------------------------------------------------------
const draft = reactive(createDraft(currentLocale(), window.location.hostname))
const step = ref<SetupStepId>('code')
const reached = ref(0)
const stepErrors = ref<Partial<Record<SetupStepId, Record<string, string>>>>({})
const submitting = ref(false)
const finishError = ref('')
const heading = ref<HTMLElement | null>(null)

async function goTo(next: SetupStepId | undefined): Promise<void> {
  if (!next) return
  step.value = next
  reached.value = Math.max(reached.value, steps.value.indexOf(next))
  // Move focus to the new step's title, so keyboard and screen-reader users start at the top of it.
  await nextTick()
  heading.value?.focus()
}

function clearErrors(stepId: SetupStepId): void {
  stepErrors.value = { ...stepErrors.value, [stepId]: undefined }
}

function onCode(code: string): void {
  draft.setupCode = code
  clearErrors('code')
  void goTo(nextStep(steps.value, 'code'))
}

function onDatabase(database: DatabaseDraft, forward: boolean): void {
  draft.database = database
  void goTo(forward ? nextStep(steps.value, 'database') : previousStep(steps.value, 'database'))
}

/** The server refused the setup code while testing the database: back to the first step, with the message. */
function onDatabaseInvalidCode(database: DatabaseDraft): void {
  draft.database = database
  stepErrors.value = { ...stepErrors.value, code: { setupCode: t('errors.setup.invalid_code') } }
  void goTo('code')
}

/** The chosen database already holds a home that was set up (a reinstall): sign in instead. */
async function onAlreadySetUp(): Promise<void> {
  toast.info(t('setup.database.alreadySetUp'))
  await router.replace({ name: 'login' })
}

function onAdmin(admin: AdminDraft, forward: boolean): void {
  draft.admin = admin
  if (forward) clearErrors('admin')
  void goTo(forward ? nextStep(steps.value, 'admin') : previousStep(steps.value, 'admin'))
}

function onHome(home: HomeDraft, forward: boolean): void {
  draft.home = home
  if (forward) clearErrors('home')
  void goTo(forward ? nextStep(steps.value, 'home') : previousStep(steps.value, 'home'))
}

async function finish(): Promise<void> {
  submitting.value = true
  finishError.value = ''
  try {
    const me = await session.completeSetup(toRequest(draft))
    toast.success(t('setup.done', { name: me.displayName }))
    await router.replace({ name: 'dashboard' })
  } catch (error) {
    const code = problem.codeOf(error)
    if (code === 'setup.invalid_code') {
      stepErrors.value = { ...stepErrors.value, code: { setupCode: t('errors.setup.invalid_code') } }
      await goTo('code')
      return
    }
    if (code === 'setup.already_completed') {
      toast.info(problem.message(error))
      await session.loadStatus()
      await router.replace({ name: 'login' })
      return
    }
    const fieldErrors = fieldErrorsOf(error)
    if (fieldErrors) {
      const byStep = errorsByStep(Object.keys(fieldErrors))
      const next: Partial<Record<SetupStepId, Record<string, string>>> = {}
      for (const [stepId, fields] of Object.entries(byStep) as [SetupStepId, string[]][]) {
        next[stepId] = Object.fromEntries(fields.map((field) => [field, t('validation.invalid')]))
      }
      stepErrors.value = next
      const first = firstStepWithErrors(byStep, steps.value)
      if (first) {
        toast.error(problem.message(error))
        await goTo(first)
        return
      }
    }
    finishError.value = problem.message(error)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="w-full max-w-2xl">
    <header class="mb-8 space-y-3">
      <p class="eyebrow">
        {{ t('setup.eyebrow') }}
      </p>
      <h1 class="font-serif text-4xl font-medium tracking-tight text-balance sm:text-5xl">
        {{ t('setup.heading') }}
      </h1>
      <p class="max-w-xl text-pretty text-muted-foreground">{{ t('setup.intro') }}</p>
    </header>

    <DatabaseWaiting
      v-if="phase === 'waiting'"
      :state="status?.databaseState ?? ''"
      :checking="checking"
      @recheck="recheck"
    />

    <template v-else>
      <SetupStepper class="mb-6" :steps="steps" :current="step" :reached="reached" @go="goTo" />
      <Card class="gap-0 py-0 shadow-md">
        <CardContent class="p-6 sm:p-8">
          <div class="mb-7 space-y-1.5">
            <h2
              ref="heading"
              tabindex="-1"
              class="font-serif text-2xl font-medium tracking-tight outline-none sm:text-[1.7rem]"
            >
              {{ t(`setup.steps.${step}.title`) }}
            </h2>
            <p class="text-pretty text-muted-foreground">{{ t(`setup.steps.${step}.description`) }}</p>
          </div>
          <div
            :key="step"
            class="motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-right-2"
          >
            <CodeStep
              v-if="step === 'code'"
              :model-value="draft.setupCode"
              :errors="stepErrors.code"
              @next="onCode"
            />
            <DatabaseStep
              v-else-if="step === 'database'"
              :model-value="draft.database"
              :setup-code="draft.setupCode"
              @next="(database) => onDatabase(database, true)"
              @back="(database) => onDatabase(database, false)"
              @invalid-code="onDatabaseInvalidCode"
              @already-set-up="onAlreadySetUp"
            />
            <AdminStep
              v-else-if="step === 'admin'"
              :model-value="draft.admin"
              :errors="stepErrors.admin"
              @next="(admin) => onAdmin(admin, true)"
              @back="(admin) => onAdmin(admin, false)"
            />
            <HomeStep
              v-else-if="step === 'home'"
              :model-value="draft.home"
              :errors="stepErrors.home"
              @next="(home) => onHome(home, true)"
              @back="(home) => onHome(home, false)"
            />
            <ReviewStep
              v-else
              :draft="draft"
              :busy="submitting"
              :error="finishError"
              @edit="goTo"
              @back="goTo(previousStep(steps, 'review'))"
              @finish="finish"
            />
          </div>
        </CardContent>
      </Card>
    </template>
  </div>
</template>
