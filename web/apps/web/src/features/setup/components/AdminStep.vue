<script setup lang="ts">
import {
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  useForm,
  z,
} from '@controlhome/ui'
import { onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import {
  DISPLAY_NAME_MAX_LENGTH,
  newPasswordField,
  requiredText,
  userNameField,
} from '../../../lib/validation'
import type { AdminDraft } from '../setup'
import StepFooter from './StepFooter.vue'

const props = defineProps<{ modelValue: AdminDraft; errors?: Record<string, string> }>()
const emit = defineEmits<{ next: [admin: AdminDraft]; back: [admin: AdminDraft] }>()
const { t } = useI18n()

const schema = useLocalizedSchema((t) =>
  z
    .object({
      displayName: requiredText(t, DISPLAY_NAME_MAX_LENGTH),
      userName: userNameField(t),
      password: newPasswordField(t),
      confirmPassword: z.string({ required_error: t('validation.required') }),
    })
    .superRefine((value, context) => {
      if (value.confirmPassword !== value.password)
        context.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['confirmPassword'],
          message: t('validation.passwordMismatch'),
        })
    }),
)
const { handleSubmit, setErrors, values } = useForm({
  validationSchema: schema,
  initialValues: { ...props.modelValue },
})
// Server errors for this step: applied once the fields exist (after mount), and again when they change.
const applyErrors = () => {
  if (props.errors) setErrors(props.errors)
}
onMounted(applyErrors)
watch(() => props.errors, applyErrors)

const onSubmit = handleSubmit((admin) => emit('next', admin))
const goBack = () => emit('back', { ...props.modelValue, ...values } as AdminDraft)
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <div class="grid items-start gap-6 sm:grid-cols-2">
      <FormField v-slot="{ componentField }" name="displayName">
        <FormItem>
          <FormLabel>{{ t('setup.admin.displayName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="name" />
          </FormControl>
          <FormDescription>{{ t('setup.admin.displayNameHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="userName">
        <FormItem>
          <FormLabel>{{ t('setup.admin.userName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="username" autocapitalize="off" spellcheck="false" />
          </FormControl>
          <FormDescription>{{ t('setup.admin.userNameHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>
    <FormField v-slot="{ componentField }" name="password">
      <FormItem>
        <FormLabel>{{ t('setup.admin.password') }}</FormLabel>
        <FormControl>
          <PasswordInput v-bind="componentField" autocomplete="new-password" />
        </FormControl>
        <FormDescription>{{ t('setup.admin.passwordHelp') }}</FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>
    <FormField v-slot="{ componentField }" name="confirmPassword">
      <FormItem>
        <FormLabel>{{ t('setup.admin.confirmPassword') }}</FormLabel>
        <FormControl>
          <PasswordInput v-bind="componentField" autocomplete="new-password" />
        </FormControl>
        <FormMessage />
      </FormItem>
    </FormField>
    <StepFooter can-go-back @back="goBack" />
  </form>
</template>
