<script setup lang="ts">
import { createUser, updateUser, type UserDto } from '@controlhome/api-client'
import {
  Button,
  DialogClose,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  RadioGroup,
  RadioGroupItem,
  Switch,
  useForm,
  z,
} from '@controlhome/ui'
import { ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import {
  DISPLAY_NAME_MAX_LENGTH,
  newPasswordField,
  requiredText,
  userNameField,
} from '../../../lib/validation'
import { ROLES } from '../users'

/** Create (user = null) or edit form, rendered inside a dialog. `self`: the signed-in user's own account. */
const props = defineProps<{ user: UserDto | null; self?: boolean }>()
const emit = defineEmits<{ saved: [user: UserDto, created: boolean] }>()
const { t } = useI18n()
const problem = useProblem()
const roleLabelId = useId()

const editing = props.user !== null

const schema = useLocalizedSchema((t) =>
  z.object({
    displayName: requiredText(t, DISPLAY_NAME_MAX_LENGTH),
    role: z.enum(ROLES),
    userName: editing ? z.string().optional() : userNameField(t),
    password: editing ? z.string().optional() : newPasswordField(t),
    isDisabled: z.boolean(),
  }),
)
const { handleSubmit, isSubmitting, setFieldError } = useForm({
  validationSchema: schema,
  initialValues: {
    displayName: props.user?.displayName ?? '',
    role: (props.user?.role as (typeof ROLES)[number] | undefined) ?? 'Member',
    userName: props.user?.userName ?? '',
    password: '',
    isDisabled: props.user?.isDisabled ?? false,
  },
})

const formError = ref('')

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    const saved = props.user
      ? unwrap(
          await updateUser({
            path: { id: props.user.id },
            body: { displayName: values.displayName, role: values.role, isDisabled: values.isDisabled },
          }),
        )
      : unwrap(
          await createUser({
            body: {
              userName: values.userName ?? '',
              displayName: values.displayName,
              password: values.password ?? '',
              role: values.role,
            },
          }),
        )
    emit('saved', saved, !props.user)
  } catch (error) {
    if (problem.codeOf(error) === 'users.duplicate_username') {
      setFieldError('userName', problem.message(error))
      return
    }
    const fields = {
      userName: 'userName',
      displayName: 'displayName',
      password: 'password',
      role: 'role',
    } as const
    if (!problem.applyToForm(error, fields, setFieldError)) formError.value = problem.message(error)
  }
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <DialogHeader>
      <DialogTitle class="font-serif text-2xl font-medium tracking-tight">
        {{ user ? t('users.form.editTitle', { name: user.displayName }) : t('users.form.createTitle') }}
      </DialogTitle>
      <DialogDescription>
        {{ user ? t('users.form.editDescription') : t('users.form.createDescription') }}
      </DialogDescription>
    </DialogHeader>

    <FormAlert v-if="formError" :message="formError" />

    <div class="grid gap-5">
      <FormField v-slot="{ componentField }" name="displayName">
        <FormItem>
          <FormLabel>{{ t('users.form.displayName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="off" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>

      <FormField v-if="!user" v-slot="{ componentField }" name="userName">
        <FormItem>
          <FormLabel>{{ t('users.form.userName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="off" autocapitalize="off" spellcheck="false" />
          </FormControl>
          <FormDescription>{{ t('users.form.userNameHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>

      <FormField v-if="!user" v-slot="{ componentField }" name="password">
        <FormItem>
          <FormLabel>{{ t('users.form.password') }}</FormLabel>
          <FormControl>
            <PasswordInput v-bind="componentField" autocomplete="new-password" />
          </FormControl>
          <FormDescription>{{ t('users.form.passwordHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>

      <FormField v-slot="{ componentField }" name="role">
        <FormItem>
          <FormLabel :id="roleLabelId">{{ t('users.form.role') }}</FormLabel>
          <FormControl>
            <RadioGroup v-bind="componentField" :aria-labelledby="roleLabelId" class="grid gap-2">
              <label
                v-for="role in ROLES"
                :key="role"
                class="flex cursor-pointer items-start gap-3 rounded-lg border p-3 transition-colors hover:bg-accent/40 has-data-[state=checked]:border-primary has-data-[state=checked]:bg-accent/50"
              >
                <RadioGroupItem :value="role" class="mt-0.5" />
                <span class="grid gap-0.5">
                  <span class="text-sm font-medium">{{ t(`roles.${role}`) }}</span>
                  <span class="text-xs text-pretty text-muted-foreground">{{
                    t(`roles.${role}Description`)
                  }}</span>
                </span>
              </label>
            </RadioGroup>
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>

      <FormField v-if="user" v-slot="{ value, handleChange }" name="isDisabled">
        <FormItem class="flex flex-row items-start justify-between gap-4 rounded-lg border p-3">
          <div class="grid gap-1">
            <FormLabel>{{ t('users.form.disabled') }}</FormLabel>
            <FormDescription class="text-xs">{{
              self ? t('users.form.selfDisabledHelp') : t('users.form.disabledHelp')
            }}</FormDescription>
          </div>
          <FormControl>
            <Switch
              :model-value="value"
              :disabled="self"
              @update:model-value="(checked) => handleChange(checked === true)"
            />
          </FormControl>
        </FormItem>
      </FormField>
    </div>

    <DialogFooter>
      <DialogClose as-child>
        <Button type="button" variant="outline">{{ t('common.cancel') }}</Button>
      </DialogClose>
      <Button type="submit" :loading="isSubmitting">
        <template v-if="user">{{ isSubmitting ? t('users.form.saving') : t('users.form.save') }}</template>
        <template v-else>{{ isSubmitting ? t('users.form.creating') : t('users.form.create') }}</template>
      </Button>
    </DialogFooter>
  </form>
</template>
