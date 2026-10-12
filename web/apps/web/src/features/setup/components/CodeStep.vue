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
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { normalizeSetupCode } from '../setup'
import StepFooter from './StepFooter.vue'

const props = defineProps<{ modelValue: string; errors?: Record<string, string> }>()
const emit = defineEmits<{ next: [code: string] }>()
const { t } = useI18n()

const schema = useLocalizedSchema((t) =>
  z.object({
    setupCode: z
      .string({ required_error: t('validation.required') })
      .trim()
      .min(1, t('validation.required'))
      .max(64, t('validation.maxLength', { max: 64 })),
  }),
)
const { handleSubmit, setErrors } = useForm({
  validationSchema: schema,
  initialValues: { setupCode: props.modelValue },
})
// Server errors for this step: applied once the fields exist (after mount), and again when they change.
const applyErrors = () => {
  if (props.errors) setErrors(props.errors)
}
onMounted(applyErrors)
watch(() => props.errors, applyErrors)

const onSubmit = handleSubmit((values) => emit('next', normalizeSetupCode(values.setupCode)))
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <FormField v-slot="{ componentField }" name="setupCode">
      <FormItem>
        <FormLabel>{{ t('setup.code.label') }}</FormLabel>
        <FormControl>
          <Input
            v-bind="componentField"
            class="h-control-lg font-mono text-lg tracking-[0.18em] uppercase md:text-lg"
            :placeholder="t('setup.code.placeholder')"
            autocomplete="one-time-code"
            autocapitalize="characters"
            spellcheck="false"
          />
        </FormControl>
        <FormDescription>{{ t('setup.code.help') }}</FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>
    <StepFooter />
  </form>
</template>
