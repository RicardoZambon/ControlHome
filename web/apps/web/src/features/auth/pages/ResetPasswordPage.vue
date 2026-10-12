<script setup lang="ts">
import { resetPasswordWithToken } from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  AlertTitle,
  Button,
  Card,
  CardContent,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  useForm,
  z,
} from '@controlhome/ui'
import { CircleCheckBig, LinkIcon } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { newPasswordField } from '../../../lib/validation'

const { t } = useI18n()
const route = useRoute()
const problem = useProblem()

// The link printed by `reset-admin <user>`: /reset-password?user=<userName>&token=<one-time token>
const queryValue = (value: unknown) => (typeof value === 'string' ? value.trim() : '')
const userName = computed(() => queryValue(route.query.user))
const token = computed(() => queryValue(route.query.token))
const linkComplete = computed(() => userName.value !== '' && token.value !== '')

const schema = useLocalizedSchema((t) =>
  z
    .object({
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
const { handleSubmit, isSubmitting, setFieldError } = useForm({
  validationSchema: schema,
  initialValues: { newPassword: '', confirmPassword: '' },
})

const formError = ref('')
const done = ref(false)

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    unwrap(
      await resetPasswordWithToken({
        body: { userName: userName.value, token: token.value, newPassword: values.newPassword },
      }),
    )
    done.value = true
  } catch (error) {
    const onFields = problem.applyToForm(error, { newPassword: 'newPassword' }, setFieldError)
    if (!onFields) formError.value = problem.message(error)
  }
})
</script>

<template>
  <div class="w-full max-w-md">
    <header class="mb-8 space-y-2">
      <h1 class="font-serif text-4xl font-medium tracking-tight">{{ t('auth.reset.heading') }}</h1>
      <p v-if="linkComplete" class="text-muted-foreground">
        {{ t('auth.reset.subtitle', { user: userName }) }}
      </p>
    </header>

    <Alert v-if="!linkComplete" variant="destructive" role="alert">
      <LinkIcon />
      <AlertTitle>{{ t('auth.reset.incompleteTitle') }}</AlertTitle>
      <AlertDescription>{{ t('auth.reset.incompleteDescription') }}</AlertDescription>
    </Alert>

    <div v-else-if="done" class="grid gap-6" role="status">
      <Alert variant="success" role="none">
        <CircleCheckBig />
        <AlertTitle>{{ t('auth.reset.doneTitle') }}</AlertTitle>
        <AlertDescription>{{ t('auth.reset.doneDescription') }}</AlertDescription>
      </Alert>
      <Button as-child class="w-full">
        <RouterLink :to="{ name: 'login' }">{{ t('auth.reset.toLogin') }}</RouterLink>
      </Button>
    </div>

    <Card v-else class="gap-0 py-0 shadow-md">
      <CardContent class="p-6 sm:p-8">
        <form novalidate class="grid gap-5" @submit="onSubmit">
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
          <FormField v-slot="{ componentField }" name="newPassword">
            <FormItem>
              <FormLabel>{{ t('auth.reset.newPassword') }}</FormLabel>
              <FormControl>
                <PasswordInput v-bind="componentField" autocomplete="new-password" />
              </FormControl>
              <FormDescription>{{ t('auth.reset.newPasswordHelp') }}</FormDescription>
              <FormMessage />
            </FormItem>
          </FormField>
          <FormField v-slot="{ componentField }" name="confirmPassword">
            <FormItem>
              <FormLabel>{{ t('auth.reset.confirmPassword') }}</FormLabel>
              <FormControl>
                <PasswordInput v-bind="componentField" autocomplete="new-password" />
              </FormControl>
              <FormMessage />
            </FormItem>
          </FormField>
          <Button type="submit" class="mt-1 w-full" :loading="isSubmitting">
            {{ isSubmitting ? t('auth.reset.submitting') : t('auth.reset.submit') }}
          </Button>
        </form>
      </CardContent>
    </Card>
  </div>
</template>
