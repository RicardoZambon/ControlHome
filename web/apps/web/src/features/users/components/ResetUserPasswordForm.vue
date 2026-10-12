<script setup lang="ts">
import { resetUserPassword, type UserDto } from '@controlhome/api-client'
import {
  Button,
  DialogClose,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  useForm,
  z,
} from '@controlhome/ui'
import { LoaderCircle } from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { newPasswordField } from '../../../lib/validation'

const props = defineProps<{ user: UserDto }>()
const emit = defineEmits<{ done: [user: UserDto] }>()
const { t } = useI18n()
const problem = useProblem()

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

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    unwrap(
      await resetUserPassword({ path: { id: props.user.id }, body: { newPassword: values.newPassword } }),
    )
    emit('done', props.user)
  } catch (error) {
    if (!problem.applyToForm(error, { newPassword: 'newPassword' }, setFieldError))
      formError.value = problem.message(error)
  }
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <DialogHeader>
      <DialogTitle class="font-serif text-2xl font-medium tracking-tight">
        {{ t('users.reset.title', { name: user.displayName }) }}
      </DialogTitle>
      <DialogDescription>{{ t('users.reset.description') }}</DialogDescription>
    </DialogHeader>
    <FormAlert v-if="formError" :message="formError" />
    <!-- Lets password managers store the new password for the right account. -->
    <input
      type="text"
      name="username"
      autocomplete="username"
      :value="user.userName"
      class="sr-only"
      tabindex="-1"
      aria-hidden="true"
      readonly
    />
    <div class="grid gap-5">
      <FormField v-slot="{ componentField }" name="newPassword">
        <FormItem>
          <FormLabel>{{ t('users.reset.newPassword') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="new-password" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="confirmPassword">
        <FormItem>
          <FormLabel>{{ t('users.reset.confirmPassword') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="new-password" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>
    <DialogFooter>
      <DialogClose as-child>
        <Button type="button" variant="outline">{{ t('common.cancel') }}</Button>
      </DialogClose>
      <Button type="submit" :disabled="isSubmitting">
        <LoaderCircle v-if="isSubmitting" class="animate-spin" />
        {{ isSubmitting ? t('users.reset.submitting') : t('users.reset.submit') }}
      </Button>
    </DialogFooter>
  </form>
</template>
