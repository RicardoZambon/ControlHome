<script setup lang="ts">
import { changePassword } from '@controlhome/api-client'
import {
  Button,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  toast,
  useForm,
  z,
} from '@controlhome/ui'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { newPasswordField, passwordField } from '../../../lib/validation'

defineProps<{ userName: string }>()
const { t } = useI18n()
const problem = useProblem()

const schema = useLocalizedSchema((t) =>
  z
    .object({
      currentPassword: passwordField(t),
      newPassword: newPasswordField(t),
      confirmPassword: z.string({ required_error: t('validation.required') }),
    })
    .superRefine((value, context) => {
      if (value.confirmPassword !== value.newPassword)
        context.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['confirmPassword'],
          message: t('validation.passwordMismatch'),
        })
    }),
)
const { handleSubmit, isSubmitting, setFieldError, resetForm } = useForm({
  validationSchema: schema,
  initialValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
})
const formError = ref('')

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    unwrap(
      await changePassword({
        body: { currentPassword: values.currentPassword, newPassword: values.newPassword },
      }),
    )
    resetForm()
    toast.success(t('profile.password.changed'))
  } catch (error) {
    if (problem.codeOf(error) === 'auth.password_incorrect') {
      setFieldError('currentPassword', problem.message(error))
      return
    }
    const fields = { currentPassword: 'currentPassword', newPassword: 'newPassword' } as const
    if (!problem.applyToForm(error, fields, setFieldError)) formError.value = problem.message(error)
  }
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <FormAlert v-if="formError" :message="formError" />
    <!-- Lets password managers store the new password for the right account. -->
    <input
      type="text"
      name="username"
      autocomplete="username"
      :value="userName"
      class="sr-only"
      tabindex="-1"
      aria-hidden="true"
      readonly
    />
    <!-- One grid for all three fields: the current password lines up with the new one below it. -->
    <div class="grid items-start gap-6 sm:grid-cols-2">
      <FormField v-slot="{ componentField }" name="currentPassword">
        <FormItem class="sm:col-start-1">
          <FormLabel>{{ t('profile.password.current') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="current-password" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="newPassword">
        <FormItem class="sm:col-start-1">
          <FormLabel>{{ t('profile.password.new') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="new-password" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="confirmPassword">
        <FormItem>
          <FormLabel>{{ t('profile.password.confirm') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="new-password" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>
    <div class="flex flex-col border-t pt-5 sm:flex-row sm:justify-end">
      <Button type="submit" variant="secondary" :loading="isSubmitting">
        {{ isSubmitting ? t('profile.password.submitting') : t('profile.password.submit') }}
      </Button>
    </div>
  </form>
</template>
