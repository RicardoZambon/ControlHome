<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { computed, ref, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import SchemaField from './SchemaField.vue'
import { numberBounds, validateFields, type SchemaErrorKind, type SchemaFieldDefinition } from './schema'

/**
 * A form built from field schemas (connector configuration, flow steps, command arguments): one `<SchemaField>` per
 * field, client-side validation that mirrors the server's, and server field errors (`errors`, already translated) shown
 * on their fields. Emits `submit` with the values once they are valid.
 */
const props = defineProps<{
  fields: readonly SchemaFieldDefinition[]
  modelValue: Record<string, unknown>
  /** Translated messages by field key (e.g. from a `validation.failed` problem). */
  errors?: Record<string, string | undefined>
  disabled?: boolean
  busy?: boolean
  submitLabel?: string
  /** Labels of enum values, flags and events. */
  optionLabel?: (field: SchemaFieldDefinition, value: string) => string
  class?: HTMLAttributes['class']
}>()

const emit = defineEmits<{
  'update:modelValue': [value: Record<string, unknown>]
  submit: [value: Record<string, unknown>]
}>()

const { t } = useI18n()
const localErrors = ref<Record<string, SchemaErrorKind>>({})
const submitted = ref(false)

function messageFor(field: SchemaFieldDefinition, kind: SchemaErrorKind): string {
  switch (kind) {
    case 'required':
      return t('validation.required')
    case 'invalid_type':
      return field.schema.type === 'number' ? t('validation.number') : t('validation.invalid')
    case 'out_of_range': {
      const bounds = field.schema.type === 'number' ? numberBounds(field.schema, field.constraint) : undefined
      if (bounds?.min !== undefined && bounds.max !== undefined)
        return t('schema.errors.between', { min: bounds.min, max: bounds.max })
      if (bounds?.min !== undefined) return t('schema.errors.atLeast', { min: bounds.min })
      if (bounds?.max !== undefined) return t('schema.errors.atMost', { max: bounds.max })
      return t('validation.invalid')
    }
    case 'not_on_step': {
      const step =
        field.schema.type === 'number' ? numberBounds(field.schema, field.constraint).step : undefined
      return step ? t('schema.errors.step', { step }) : t('validation.invalid')
    }
    case 'not_allowed':
      return t('schema.errors.notAllowed')
    case 'too_long':
      return t('validation.maxLength', {
        max: field.schema.type === 'string' ? (field.schema.maxLength ?? 0) : 0,
      })
  }
}

const shownErrors = computed(() => {
  const result: Record<string, string> = {}
  for (const field of props.fields) {
    const local = submitted.value ? localErrors.value[field.key] : undefined
    const message = local ? messageFor(field, local) : props.errors?.[field.key]
    if (message) result[field.key] = message
  }
  return result
})

function update(key: string, value: unknown): void {
  const next = { ...props.modelValue, [key]: value }
  emit('update:modelValue', next)
  if (submitted.value) localErrors.value = validateFields(props.fields, next)
}

function onSubmit(): void {
  submitted.value = true
  localErrors.value = validateFields(props.fields, props.modelValue)
  if (Object.keys(localErrors.value).length > 0) return
  const values: Record<string, unknown> = {}
  for (const field of props.fields) {
    const value = props.modelValue[field.key]
    if (!field.readonly && value !== undefined && value !== '') values[field.key] = value
  }
  emit('submit', values)
}
</script>

<template>
  <form novalidate :class="cn('grid gap-5', props.class)" data-slot="schema-form" @submit.prevent="onSubmit">
    <SchemaField
      v-for="field in fields"
      :key="field.key"
      :schema="field.schema"
      :model-value="modelValue[field.key]"
      :label="field.label"
      :help="field.help"
      :required="field.required"
      :secret="field.secret"
      :readonly="field.readonly"
      :constraint="field.constraint"
      :disabled="disabled || busy"
      :error="shownErrors[field.key]"
      :option-label="optionLabel ? (value: string) => optionLabel!(field, value) : undefined"
      @update:model-value="(value) => update(field.key, value)"
    />
    <slot name="actions" :busy="busy">
      <div class="flex justify-end">
        <Button type="submit" :disabled="disabled" :loading="busy">
          {{ submitLabel ?? t('common.save') }}
        </Button>
      </div>
    </slot>
  </form>
</template>
