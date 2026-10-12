<script setup lang="ts">
import {
  Button,
  Card,
  CardContent,
  Checkbox,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  useForm,
  z,
} from '@controlhome/ui'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { safeRedirect } from '../../../lib/redirect'
import { passwordField, requiredText } from '../../../lib/validation'
import { useSessionStore } from '../../../stores/session'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSessionStore()
const problem = useProblem()

const schema = useLocalizedSchema((t) =>
  z.object({
    userName: requiredText(t, 256),
    password: passwordField(t),
    rememberMe: z.boolean(),
  }),
)
const { handleSubmit, isSubmitting, setFieldError } = useForm({
  validationSchema: schema,
  initialValues: { userName: '', password: '', rememberMe: true },
})

const formError = ref('')
// 'Forgot your password?' shows its help inline, under the form.
const forgotOpen = ref(false)

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    await session.login(values)
    await router.replace(safeRedirect(route.query.redirect) ?? { name: 'dashboard' })
  } catch (error) {
    const onFields = problem.applyToForm(error, { userName: 'userName', password: 'password' }, setFieldError)
    if (!onFields) formError.value = problem.message(error)
  }
})
</script>

<template>
  <div class="w-full max-w-md">
    <header class="mb-8 space-y-2">
      <h1 class="font-serif text-4xl font-medium tracking-tight">{{ t('auth.login.heading') }}</h1>
      <p class="text-muted-foreground">{{ t('auth.login.subtitle') }}</p>
    </header>

    <Card class="gap-0 py-0 shadow-md">
      <CardContent class="p-6 sm:p-8">
        <form novalidate class="grid gap-5" @submit="onSubmit">
          <FormAlert v-if="formError" :message="formError" />
          <FormField v-slot="{ componentField }" name="userName">
            <FormItem>
              <FormLabel>{{ t('auth.login.userName') }}</FormLabel>
              <FormControl>
                <Input
                  v-bind="componentField"
                  autocomplete="username"
                  autocapitalize="off"
                  spellcheck="false"
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          </FormField>
          <FormField v-slot="{ componentField }" name="password">
            <FormItem>
              <FormLabel>{{ t('auth.login.password') }}</FormLabel>
              <FormControl>
                <PasswordInput v-bind="componentField" autocomplete="current-password" />
              </FormControl>
              <FormMessage />
            </FormItem>
          </FormField>
          <FormField v-slot="{ value, handleChange }" name="rememberMe">
            <FormItem class="flex flex-row items-center gap-3">
              <FormControl>
                <Checkbox
                  :model-value="value"
                  @update:model-value="(checked) => handleChange(checked === true)"
                />
              </FormControl>
              <FormLabel class="font-normal">{{ t('auth.login.rememberMe') }}</FormLabel>
            </FormItem>
          </FormField>
          <Button type="submit" class="mt-1 w-full" :loading="isSubmitting">
            {{ isSubmitting ? t('auth.login.submitting') : t('auth.login.submit') }}
          </Button>
        </form>

        <div class="mt-4 grid justify-items-center gap-1 text-center">
          <Button
            type="button"
            variant="link"
            size="sm"
            class="text-sm font-normal"
            aria-controls="login-forgot-help"
            :aria-expanded="forgotOpen"
            @click="forgotOpen = !forgotOpen"
          >
            {{ t('auth.login.forgot') }}
          </Button>
          <p v-show="forgotOpen" id="login-forgot-help" class="text-sm text-muted-foreground">
            {{ t('auth.login.forgotHelp') }}
          </p>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
