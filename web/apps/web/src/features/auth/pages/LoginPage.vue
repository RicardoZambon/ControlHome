<script setup lang="ts">
import {
  Button,
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
import { ChevronRight, LoaderCircle } from '@lucide/vue'
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
  <div class="w-full max-w-sm">
    <div class="mb-8 space-y-2">
      <h1 class="font-serif text-4xl font-medium tracking-tight">{{ t('auth.login.heading') }}</h1>
      <p class="text-muted-foreground">{{ t('auth.login.subtitle') }}</p>
    </div>

    <form novalidate class="grid gap-5" @submit="onSubmit">
      <FormAlert v-if="formError" :message="formError" />
      <FormField v-slot="{ componentField }" name="userName">
        <FormItem>
          <FormLabel>{{ t('auth.login.userName') }}</FormLabel>
          <FormControl>
            <Input
              v-bind="componentField"
              class="h-10"
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
            <PasswordInput v-bind="componentField" class="[&_input]:h-10" autocomplete="current-password" />
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
      <Button type="submit" size="lg" class="mt-1 w-full" :disabled="isSubmitting">
        <LoaderCircle v-if="isSubmitting" class="animate-spin" />
        {{ isSubmitting ? t('auth.login.submitting') : t('auth.login.submit') }}
      </Button>
    </form>

    <details class="group mt-8 rounded-lg border bg-card/60 px-4 py-3 text-sm">
      <summary
        class="flex cursor-pointer list-none items-center gap-2 rounded-sm font-medium outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background [&::-webkit-details-marker]:hidden"
      >
        <ChevronRight
          class="size-4 text-muted-foreground transition-transform group-open:rotate-90"
          aria-hidden="true"
        />
        {{ t('auth.login.forgot') }}
      </summary>
      <p class="mt-2 pl-6 text-pretty text-muted-foreground">{{ t('auth.login.forgotHelp') }}</p>
    </details>
  </div>
</template>
