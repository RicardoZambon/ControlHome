<script setup lang="ts">
import { getSafetyPin, setSafetyPin, type SafetyPinStatusDto } from '@controlhome/api-client'
import {
  Badge,
  Button,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Skeleton,
  toast,
  useForm,
  z,
} from '@controlhome/ui'
import { CircleCheck, CircleDashed, Lock } from '@lucide/vue'
import { computed, onMounted, ref, shallowRef } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'

/**
 * The signed-in user's safety PIN (docs/architecture.md §8): 4–8 digits that confirm safety-critical actions such as
 * unlocking a door. Shows whether one is set (and a lockout after too many wrong PINs); changing it needs the current
 * one (`GET` / `PUT /api/auth/me/pin`).
 */
const PIN_PATTERN = /^\d{4,8}$/

const { t } = useI18n()
const problem = useProblem()

const status = shallowRef<SafetyPinStatusDto | null>(null)
const statusError = ref('')
const isSet = computed(() => status.value?.isSet === true)
const lockedMinutes = computed(() =>
  status.value?.lockedForSeconds ? Math.max(1, Math.ceil(status.value.lockedForSeconds / 60)) : 0,
)

async function loadStatus(): Promise<void> {
  try {
    status.value = unwrap(await getSafetyPin())
    statusError.value = ''
  } catch (error) {
    statusError.value = problem.message(error)
  }
}
onMounted(loadStatus)

const schema = useLocalizedSchema((t) => {
  const pin = z
    .string({ required_error: t('validation.required') })
    .regex(PIN_PATTERN, t('profile.pin.format'))
  return z
    .object({
      currentPin: z.string().optional(),
      newPin: pin,
      confirmPin: z.string({ required_error: t('validation.required') }),
    })
    .superRefine((value, context) => {
      if (isSet.value && !PIN_PATTERN.test(value.currentPin ?? ''))
        context.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['currentPin'],
          message: value.currentPin ? t('profile.pin.format') : t('validation.required'),
        })
      if (value.confirmPin !== value.newPin)
        context.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['confirmPin'],
          message: t('profile.pin.mismatch'),
        })
    })
})

const { handleSubmit, isSubmitting, setFieldError, resetForm } = useForm({
  validationSchema: schema,
  initialValues: { currentPin: '', newPin: '', confirmPin: '' },
})
const formError = ref('')

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  const changing = isSet.value
  try {
    unwrap(
      await setSafetyPin({
        body: { currentPin: changing ? (values.currentPin ?? null) : null, newPin: values.newPin },
      }),
    )
    resetForm()
    toast.success(changing ? t('profile.pin.changed') : t('profile.pin.saved'))
    await loadStatus()
  } catch (error) {
    const code = problem.codeOf(error)
    if (code === 'safety.pin_incorrect') {
      setFieldError('currentPin', problem.message(error))
      return
    }
    if (code === 'safety.locked') await loadStatus()
    const fields = { currentPin: 'currentPin', newPin: 'newPin' } as const
    if (!problem.applyToForm(error, fields, setFieldError, { newPin: t('profile.pin.format') }))
      formError.value = problem.message(error)
  }
})
</script>

<template>
  <div class="grid gap-6">
    <div class="flex flex-wrap items-center gap-2" role="status">
      <Skeleton v-if="!status && !statusError" class="h-6 w-28 rounded-full" />
      <template v-else-if="status">
        <Badge v-if="isSet" variant="outline" class="gap-1.5 border-state-on/50 bg-card py-1">
          <CircleCheck class="text-state-on" aria-hidden="true" />
          {{ t('profile.pin.isSet') }}
        </Badge>
        <Badge v-else variant="outline" class="gap-1.5 bg-card py-1 text-muted-foreground">
          <CircleDashed aria-hidden="true" />
          {{ t('profile.pin.notSet') }}
        </Badge>
        <Badge v-if="lockedMinutes > 0" variant="outline" class="gap-1.5 border-state-warn/60 bg-card py-1">
          <Lock class="text-state-warn" aria-hidden="true" />
          {{ t('profile.pin.locked', { minutes: lockedMinutes }) }}
        </Badge>
      </template>
    </div>
    <FormAlert v-if="statusError" :message="statusError" />

    <form novalidate class="grid gap-6" data-slot="safety-pin-form" @submit="onSubmit">
      <FormAlert v-if="formError" :message="formError" />
      <div class="grid items-start gap-6 sm:grid-cols-2">
        <FormField v-if="isSet" v-slot="{ componentField }" name="currentPin">
          <FormItem class="sm:col-start-1">
            <FormLabel>{{ t('profile.pin.current') }}</FormLabel>
            <FormControl>
              <PasswordInput v-bind="componentField" inputmode="numeric" autocomplete="off" maxlength="8" />
            </FormControl>
            <FormMessage />
          </FormItem>
        </FormField>
        <FormField v-slot="{ componentField }" name="newPin">
          <FormItem class="sm:col-start-1">
            <FormLabel>{{ isSet ? t('profile.pin.new') : t('profile.pin.pin') }}</FormLabel>
            <FormControl>
              <PasswordInput v-bind="componentField" inputmode="numeric" autocomplete="off" maxlength="8" />
            </FormControl>
            <FormDescription>{{ t('profile.pin.help') }}</FormDescription>
            <FormMessage />
          </FormItem>
        </FormField>
        <FormField v-slot="{ componentField }" name="confirmPin">
          <FormItem>
            <FormLabel>{{ t('profile.pin.confirm') }}</FormLabel>
            <FormControl>
              <PasswordInput v-bind="componentField" inputmode="numeric" autocomplete="off" maxlength="8" />
            </FormControl>
            <FormMessage />
          </FormItem>
        </FormField>
      </div>
      <div class="flex flex-col border-t pt-5 sm:flex-row sm:justify-end">
        <Button type="submit" variant="secondary" :loading="isSubmitting">
          {{ isSubmitting ? t('common.saving') : isSet ? t('profile.pin.change') : t('profile.pin.set') }}
        </Button>
      </div>
    </form>
  </div>
</template>
