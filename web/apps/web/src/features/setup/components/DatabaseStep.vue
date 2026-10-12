<script setup lang="ts">
import {
  Alert,
  AlertDescription,
  AlertTitle,
  Button,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Switch,
  cn,
  toast,
  useForm,
  z,
} from '@controlhome/ui'
import { Check, CircleCheck, Copy, LoaderCircle, ScrollText, TriangleAlert } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { copyToClipboard } from '../../../lib/clipboard'
import { fieldErrorsOf, problemOf } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'
import {
  DATABASE_HOST_MAX_LENGTH,
  DATABASE_HOST_PATTERN,
  DATABASE_NAME_PATTERN,
  SQL_NAME_MAX_LENGTH,
  databaseErrorsByStep,
  databaseProgress,
  toDatabaseRequest,
  type DatabaseDraft,
} from '../setup'
import StepFooter from './StepFooter.vue'

/**
 * Pre-database setup: the SQL Server connection. "Connect" tests and saves it (`POST /api/setup/database`, with the setup
 * code from the first step); the server then prepares the database while this step follows its progress, and moves on
 * once it is ready. When the login may not create the database, the T-SQL to run is shown with "Check again".
 */
const props = defineProps<{ modelValue: DatabaseDraft; setupCode: string }>()
const emit = defineEmits<{
  next: [database: DatabaseDraft]
  back: [database: DatabaseDraft]
  invalidCode: [database: DatabaseDraft]
  alreadySetUp: []
}>()
const { t, te } = useI18n()
const session = useSessionStore()
const problem = useProblem()

type DatabaseField = 'host' | 'port' | 'database' | 'userName' | 'password'

const schema = useLocalizedSchema((t) => {
  const required = () =>
    z
      .string({ required_error: t('validation.required') })
      .trim()
      .min(1, t('validation.required'))
  return z.object({
    host: required()
      .max(DATABASE_HOST_MAX_LENGTH, t('validation.maxLength', { max: DATABASE_HOST_MAX_LENGTH }))
      .regex(DATABASE_HOST_PATTERN, t('setup.database.hostInvalid')),
    port: required()
      .regex(/^\d{1,5}$/, t('setup.database.portInvalid'))
      .refine((value) => Number(value) >= 1 && Number(value) <= 65535, t('setup.database.portInvalid')),
    database: required().regex(DATABASE_NAME_PATTERN, t('setup.database.nameInvalid')),
    userName: required().max(SQL_NAME_MAX_LENGTH, t('validation.maxLength', { max: SQL_NAME_MAX_LENGTH })),
    password: z
      .string({ required_error: t('validation.required') })
      .min(1, t('validation.required'))
      .max(SQL_NAME_MAX_LENGTH, t('validation.maxLength', { max: SQL_NAME_MAX_LENGTH })),
    trustServerCertificate: z.boolean(),
  })
})
const { handleSubmit, setFieldError, values, isSubmitting } = useForm({
  validationSchema: schema,
  initialValues: { ...props.modelValue },
})

const current = (): DatabaseDraft => ({ ...props.modelValue, ...values }) as DatabaseDraft

// --- Form ------------------------------------------------------------------------------------------------------------
type View = 'form' | 'progress' | 'connected'
const view = ref<View>(
  session.status?.databaseConfigured
    ? session.status.databaseState === 'Ready'
      ? 'connected'
      : 'progress'
    : 'form',
)
const formError = ref<{ title?: string; message: string } | null>(null)
const script = ref<string | null>(null)
const scriptSection = ref<HTMLElement | null>(null)
const errorAlert = ref<InstanceType<typeof FormAlert> | null>(null)

const onSubmit = handleSubmit(async (database) => {
  formError.value = null
  try {
    await session.configureDatabase(toDatabaseRequest(props.setupCode, database as DatabaseDraft))
    script.value = null
    follow()
  } catch (error) {
    await showFailure(error)
  }
})

async function showFailure(error: unknown): Promise<void> {
  const code = problem.codeOf(error)
  if (code === 'setup.invalid_code') {
    emit('invalidCode', current())
    return
  }
  if (code === 'db.already_configured') {
    await session.loadStatus()
    follow()
    return
  }
  if (code === 'db.create_denied' && problemOf(error)?.script) {
    script.value = problemOf(error)?.script ?? null
    await nextTick()
    scriptSection.value?.focus()
    return
  }

  script.value = null
  const fields = fieldErrorsOf(error)
  if (fields) {
    const byStep = databaseErrorsByStep(Object.keys(fields))
    if (byStep.code) {
      emit('invalidCode', current())
      return
    }
    for (const field of byStep.database ?? []) setFieldError(field as DatabaseField, t('validation.invalid'))
  }
  const reason = problemOf(error)?.reason
  const detail =
    reason && te(`setup.database.reasons.${reason}`) ? t(`setup.database.reasons.${reason}`) : undefined
  formError.value = detail
    ? { title: t('setup.database.connectFailed'), message: detail }
    : { message: problem.message(error) }
  // The form is long: bring the message into view from the button that was just pressed (it is announced anyway).
  await nextTick()
  const alert = errorAlert.value?.$el as HTMLElement | undefined
  alert?.scrollIntoView?.({ block: 'nearest' })
}

async function copyScript(): Promise<void> {
  if (script.value && (await copyToClipboard(script.value)))
    toast.success(t('setup.database.createDenied.copied'))
}

// --- Progress: follow GET /api/setup/status until the database is ready ----------------------------------------------
const CONNECT_ERRORS = ['db.connection_failed', 'sql.version_unsupported']
const STAGES = ['connecting', 'preparing', 'ready'] as const
type StageState = 'done' | 'active' | 'failed' | 'pending'

const progress = computed(() => databaseProgress(session.status?.databaseState ?? 'Connecting'))
const failureCode = computed(() => session.status?.databaseError ?? 'unknown')
const stages = computed<{ key: string; state: StageState }[]>(() => {
  const now = progress.value
  const at =
    now === 'failed' ? (CONNECT_ERRORS.includes(failureCode.value) ? 'connecting' : 'preparing') : now
  const index = STAGES.indexOf(at)
  return [
    { key: 'saved', state: 'done' },
    ...STAGES.map((key, position) => ({
      key,
      state: (position < index
        ? 'done'
        : position > index
          ? 'pending'
          : now === 'failed'
            ? 'failed'
            : now === 'ready'
              ? 'done'
              : 'active') as StageState,
    })),
  ]
})
const failureMessage = computed(() =>
  te(`errors.${failureCode.value}`) ? t(`errors.${failureCode.value}`) : t('errors.unknown'),
)

let timer: ReturnType<typeof setTimeout> | undefined
let active = true

function follow(): void {
  view.value = 'progress'
  schedule(0)
}

function schedule(delay: number): void {
  clearTimeout(timer)
  timer = setTimeout(() => void poll(), delay)
}

async function poll(): Promise<void> {
  const status = await session.loadStatus().catch(() => session.status)
  if (!active) return
  const now = databaseProgress(status?.databaseState ?? 'Connecting')
  if (now === 'ready') {
    finish()
    return
  }
  // A failed initialization is retried by the server every minute: keep following it, more slowly.
  schedule(now === 'failed' ? 5000 : 1500)
}

function finish(): void {
  if (session.status?.setupCompleted) {
    emit('alreadySetUp')
    return
  }
  view.value = 'connected'
  toast.success(t('setup.database.ready'))
  // The password is not needed any more: keep it out of the draft.
  emit('next', { ...current(), password: '' })
}

onMounted(() => {
  if (view.value === 'progress') schedule(0)
})
onBeforeUnmount(() => {
  active = false
  clearTimeout(timer)
})

const stageClasses: Record<StageState, string> = {
  done: 'bg-primary text-primary-foreground',
  active: 'bg-accent text-accent-foreground',
  failed: 'bg-destructive text-destructive-foreground',
  pending: 'bg-muted text-muted-foreground',
}
</script>

<template>
  <form v-if="view === 'form'" novalidate class="grid gap-6" @submit="onSubmit">
    <FormAlert v-if="formError" ref="errorAlert" :title="formError.title" :message="formError.message" />

    <div class="grid items-start gap-6 sm:grid-cols-[minmax(0,1fr)_7.5rem]">
      <FormField v-slot="{ componentField }" name="host">
        <FormItem>
          <FormLabel>{{ t('setup.database.host') }}</FormLabel>
          <FormControl>
            <Input
              v-bind="componentField"
              autocomplete="off"
              autocapitalize="off"
              spellcheck="false"
              inputmode="url"
            />
          </FormControl>
          <FormDescription>{{ t('setup.database.hostHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="port">
        <FormItem>
          <FormLabel>{{ t('setup.database.port') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="off" inputmode="numeric" class="font-mono" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>

    <FormField v-slot="{ componentField }" name="database">
      <FormItem>
        <FormLabel>{{ t('setup.database.name') }}</FormLabel>
        <FormControl>
          <Input v-bind="componentField" autocomplete="off" autocapitalize="off" spellcheck="false" />
        </FormControl>
        <FormDescription>{{ t('setup.database.nameHelp') }}</FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>

    <div class="grid items-start gap-6 sm:grid-cols-2">
      <FormField v-slot="{ componentField }" name="userName">
        <FormItem>
          <FormLabel>{{ t('setup.database.userName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="off" autocapitalize="off" spellcheck="false" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="password">
        <FormItem>
          <FormLabel>{{ t('setup.database.password') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="off" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>

    <FormField v-slot="{ value, handleChange }" name="trustServerCertificate">
      <FormItem class="flex flex-row items-start justify-between gap-4 rounded-lg border p-4">
        <div class="grid gap-1">
          <FormLabel class="leading-snug">{{ t('setup.database.trust') }}</FormLabel>
          <FormDescription class="text-pretty">{{ t('setup.database.trustHelp') }}</FormDescription>
        </div>
        <FormControl>
          <Switch
            class="mt-0.5"
            :model-value="value"
            @update:model-value="(checked) => handleChange(checked === true)"
          />
        </FormControl>
      </FormItem>
    </FormField>

    <section
      v-if="script"
      ref="scriptSection"
      tabindex="-1"
      class="grid gap-3 outline-none"
      aria-labelledby="database-script-title"
    >
      <Alert variant="warning">
        <ScrollText />
        <AlertTitle id="database-script-title">
          {{ t('setup.database.createDenied.title') }}
        </AlertTitle>
        <AlertDescription class="text-pretty">{{
          t('setup.database.createDenied.description')
        }}</AlertDescription>
      </Alert>
      <div class="overflow-hidden rounded-lg border bg-muted">
        <div class="flex items-center justify-between gap-3 border-b py-1 pr-1 pl-4">
          <span class="text-xs font-medium text-muted-foreground">{{
            t('setup.database.createDenied.script')
          }}</span>
          <Button type="button" variant="ghost" size="sm" @click="copyScript">
            <Copy />
            {{ t('setup.database.createDenied.copy') }}
          </Button>
        </div>
        <pre
          class="max-h-80 overflow-auto p-4 font-mono text-xs leading-relaxed wrap-break-word whitespace-pre-wrap text-foreground"
          :aria-label="t('setup.database.createDenied.script')"
          tabindex="0"
        ><code>{{ script }}</code></pre>
      </div>
      <p class="text-sm text-pretty">{{ t('setup.database.createDenied.next') }}</p>
    </section>

    <StepFooter
      can-go-back
      :busy="isSubmitting"
      :next-label="script ? t('setup.database.checkAgain') : t('setup.database.connect')"
      :busy-label="t('setup.database.testing')"
      @back="emit('back', current())"
    />
  </form>

  <div v-else-if="view === 'progress'" class="grid gap-6">
    <ol class="grid gap-3.5" :aria-label="t('setup.steps.database.title')">
      <li v-for="stage in stages" :key="stage.key" class="flex items-center gap-3">
        <span
          :class="
            cn('flex size-8 shrink-0 items-center justify-center rounded-full', stageClasses[stage.state])
          "
          aria-hidden="true"
        >
          <Check v-if="stage.state === 'done'" class="size-4" />
          <LoaderCircle v-else-if="stage.state === 'active'" class="size-4 motion-safe:animate-spin" />
          <TriangleAlert v-else-if="stage.state === 'failed'" class="size-4" />
          <span v-else class="size-1.5 rounded-full bg-current" />
        </span>
        <span
          :class="cn('text-sm', stage.state === 'pending' ? 'text-muted-foreground' : 'font-medium')"
          :aria-current="stage.state === 'active' ? 'step' : undefined"
        >
          {{ t(`setup.database.progress.${stage.key}`) }}
        </span>
      </li>
    </ol>
    <FormAlert
      v-if="progress === 'failed'"
      :title="t('setup.database.progress.failedTitle')"
      :message="`${failureMessage} ${t('setup.database.progress.failedDescription')}`"
    />
    <p
      v-else
      class="border-t pt-6 text-sm text-pretty text-muted-foreground"
      role="status"
      aria-live="polite"
    >
      {{ t(`setup.database.progress.${progress === 'preparing' ? 'preparing' : 'connecting'}`) }}…
      {{ t('setup.database.progress.hint') }}
    </p>
  </div>

  <form v-else novalidate class="grid gap-6" @submit.prevent="emit('next', { ...current(), password: '' })">
    <Alert variant="success">
      <CircleCheck />
      <AlertTitle>{{ t('setup.database.connected.title') }}</AlertTitle>
      <AlertDescription>{{ t('setup.database.connected.description') }}</AlertDescription>
    </Alert>
    <StepFooter can-go-back @back="emit('back', current())" />
  </form>
</template>
