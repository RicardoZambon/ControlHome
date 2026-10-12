<script setup lang="ts">
import type { MeDto } from '@controlhome/api-client'
import {
  Button,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  RadioGroup,
  RadioGroupItem,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  toast,
  useForm,
  z,
} from '@controlhome/ui'
import { LoaderCircle, Monitor, Moon, Sun } from '@lucide/vue'
import { ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { SUPPORTED_LOCALES, currentLocale, isAppLocale } from '../../../i18n'
import { isThemeMode } from '../../../lib/preferences'
import { DISPLAY_NAME_MAX_LENGTH, requiredText } from '../../../lib/validation'
import { useSessionStore } from '../../../stores/session'

const props = defineProps<{ me: MeDto }>()
const { t } = useI18n()
const session = useSessionStore()
const problem = useProblem()
const themeLabelId = useId()

const localeOptions = [
  { value: 'en', labelKey: 'locales.en' },
  { value: 'pt-BR', labelKey: 'locales.ptBR' },
] as const
const themeOptions = [
  { value: 'light', icon: Sun, labelKey: 'theme.light' },
  { value: 'dark', icon: Moon, labelKey: 'theme.dark' },
  { value: 'system', icon: Monitor, labelKey: 'theme.system' },
] as const

const schema = useLocalizedSchema((t) =>
  z.object({
    displayName: requiredText(t, DISPLAY_NAME_MAX_LENGTH),
    locale: z.enum(SUPPORTED_LOCALES),
    theme: z.enum(['light', 'dark', 'system']),
  }),
)
const valuesOf = (me: MeDto) => ({
  displayName: me.displayName,
  locale: isAppLocale(me.locale) ? me.locale : currentLocale(),
  theme: isThemeMode(me.theme) ? me.theme : ('system' as const),
})
const { handleSubmit, isSubmitting, setFieldError, resetForm, meta } = useForm({
  validationSchema: schema,
  initialValues: valuesOf(props.me),
})
// The language or theme may change from the top bar while this page is open: follow it unless the user is editing.
watch(
  () => props.me,
  (me) => {
    if (!meta.value.dirty) resetForm({ values: valuesOf(me) })
  },
)
const formError = ref('')

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    // The store applies the saved language and theme (setLocale / setMode) once the server accepted them.
    const saved = await session.updateProfile(values)
    resetForm({ values: { ...values, displayName: saved.displayName } })
    toast.success(t('profile.preferences.saved'))
  } catch (error) {
    const fields = { displayName: 'displayName', locale: 'locale', theme: 'theme' } as const
    if (!problem.applyToForm(error, fields, setFieldError)) formError.value = problem.message(error)
  }
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <FormAlert v-if="formError" :message="formError" />
    <div class="grid items-start gap-6 sm:grid-cols-2">
      <FormField v-slot="{ componentField }" name="displayName">
        <FormItem>
          <FormLabel>{{ t('profile.preferences.displayName') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" autocomplete="name" />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="locale">
        <FormItem>
          <FormLabel>{{ t('profile.preferences.language') }}</FormLabel>
          <Select v-bind="componentField">
            <FormControl>
              <SelectTrigger class="w-full">
                <SelectValue />
              </SelectTrigger>
            </FormControl>
            <SelectContent>
              <SelectItem
                v-for="option in localeOptions"
                :key="option.value"
                :value="option.value"
                :lang="option.value"
              >
                {{ t(option.labelKey) }}
              </SelectItem>
            </SelectContent>
          </Select>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>
    <FormField v-slot="{ componentField }" name="theme">
      <FormItem>
        <FormLabel :id="themeLabelId">{{ t('profile.preferences.theme') }}</FormLabel>
        <FormControl>
          <RadioGroup v-bind="componentField" :aria-labelledby="themeLabelId" class="grid grid-cols-3 gap-3">
            <label
              v-for="option in themeOptions"
              :key="option.value"
              class="flex cursor-pointer flex-col items-center gap-2 rounded-xl border p-4 text-sm transition-colors hover:bg-accent/40 has-data-[state=checked]:border-primary has-data-[state=checked]:bg-accent/50"
            >
              <component :is="option.icon" class="size-5 text-muted-foreground" aria-hidden="true" />
              <span class="font-medium">{{ t(option.labelKey) }}</span>
              <RadioGroupItem :value="option.value" />
            </label>
          </RadioGroup>
        </FormControl>
        <FormDescription>{{ t('profile.preferences.themeHelp') }}</FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>
    <div class="flex justify-end border-t pt-5">
      <Button type="submit" :disabled="isSubmitting">
        <LoaderCircle v-if="isSubmitting" class="animate-spin" />
        {{ isSubmitting ? t('common.saving') : t('common.save') }}
      </Button>
    </div>
  </form>
</template>
