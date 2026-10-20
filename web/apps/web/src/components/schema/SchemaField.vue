<script setup lang="ts">
import type { ValueSchema } from '@controlhome/api-client'
import {
  Badge,
  Checkbox,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Slider,
  Switch,
  cn,
} from '@controlhome/ui'
import { computed, ref, useId, watch, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { parseDecimal } from '../../lib/validation'
import { allowedValues, humanize, numberBounds, snapNumber, type ValueConstraint } from './schema'

/**
 * One schema-driven value (docs/architecture.md §11): the control for every `ValueSchema` type — a switch (bool), a
 * number field with a slider when the range is bounded (number), a select (enum), a text field (string, password when
 * secret), checkboxes (flags), read-only JSON, and the last event (momentary). Used by `<SchemaForm>` (integration and
 * device configuration, flow steps, command arguments) and by the generic capability control.
 *
 * Emits `update:modelValue` while editing and `commit` once a change is complete (a toggle, a choice, a slider
 * released, a text field left or Enter pressed), so live controls can send one command per change.
 */
const props = defineProps<{
  schema: ValueSchema
  modelValue?: unknown
  label: string
  help?: string
  /** A translated error message (the field shows it and is marked invalid). */
  error?: string
  required?: boolean
  disabled?: boolean
  readonly?: boolean
  /** Write-only value (a password or a token). */
  secret?: boolean
  constraint?: ValueConstraint
  /** Labels of enum values, flags and events (default: the value, humanized). */
  optionLabel?: (value: string) => string
  /** Keep the label for screen readers only. */
  hideLabel?: boolean
  class?: HTMLAttributes['class']
}>()

const emit = defineEmits<{ 'update:modelValue': [value: unknown]; commit: [value: unknown] }>()

const { t, locale } = useI18n()
const id = useId()
const labelId = `${id}-label`
const helpId = `${id}-help`
const errorId = `${id}-error`

const describedBy = computed(
  () => [props.help ? helpId : '', props.error ? errorId : ''].filter(Boolean).join(' ') || undefined,
)
const optionText = (value: string) => props.optionLabel?.(value) ?? humanize(value)
const editable = computed(() => !props.readonly && !props.disabled)

function change(value: unknown, commit = true): void {
  emit('update:modelValue', value)
  if (commit) emit('commit', value)
}

// --- number --------------------------------------------------------------------------------------------------------------
const bounds = computed(() =>
  props.schema.type === 'number' ? numberBounds(props.schema, props.constraint) : undefined,
)
/** A slider makes sense for a bounded range with a reasonable number of steps. */
const sliderRange = computed(() => {
  const value = bounds.value
  if (!value || value.min === undefined || value.max === undefined || value.max <= value.min) return undefined
  const step = value.step ?? (value.integer ? 1 : (value.max - value.min) / 100)
  return (value.max - value.min) / step <= 1000 ? { min: value.min, max: value.max, step } : undefined
})
const numberFormat = computed(() => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 3 }))
const numberText = ref('')
watch(
  () => props.modelValue,
  (value) => {
    if (typeof value === 'number' && Number.isFinite(value)) {
      if (parseDecimal(numberText.value) !== value) numberText.value = String(value)
    } else if (value === null || value === undefined) {
      numberText.value = ''
    }
  },
  { immediate: true },
)

function onNumberInput(text: string | number): void {
  numberText.value = String(text)
  const parsed = parseDecimal(String(text))
  // An unparsable text is passed on as is, so validation reports it.
  emit('update:modelValue', parsed === null ? undefined : Number.isNaN(parsed) ? String(text) : parsed)
}

function onNumberChange(): void {
  const parsed = parseDecimal(numberText.value)
  emit('commit', parsed === null ? undefined : Number.isNaN(parsed) ? numberText.value : parsed)
}

const sliderValue = computed(() =>
  typeof props.modelValue === 'number' ? [props.modelValue] : [sliderRange.value?.min ?? 0],
)

function onSlide(values: number[] | undefined, commit: boolean): void {
  const value = values?.[0]
  if (value === undefined || !bounds.value) return
  const snapped = snapNumber(value, bounds.value)
  numberText.value = String(snapped)
  change(snapped, commit)
}

// --- enum, flags, momentary ----------------------------------------------------------------------------------------------
const options = computed(() => allowedValues(props.schema, props.constraint))
const flags = computed(() => (Array.isArray(props.modelValue) ? (props.modelValue as string[]) : []))

function toggleFlag(flag: string, checked: boolean): void {
  const next = checked
    ? [...flags.value.filter((entry) => entry !== flag), flag]
    : flags.value.filter((entry) => entry !== flag)
  change(props.schema.type === 'flags' ? props.schema.flags.filter((entry) => next.includes(entry)) : next)
}

// --- string --------------------------------------------------------------------------------------------------------------
const maxLength = computed(() =>
  props.schema.type === 'string' ? (props.schema.maxLength ?? undefined) : undefined,
)

// --- read-only text --------------------------------------------------------------------------------------------------------
const readText = computed(() => {
  const value = props.modelValue
  if (value === null || value === undefined || value === '') return t('schema.noValue')
  switch (props.schema.type) {
    case 'bool':
      return value ? t('schema.true') : t('schema.false')
    case 'number': {
      const unit = bounds.value?.unit
      return typeof value === 'number'
        ? `${numberFormat.value.format(value)}${unit ? ` ${unit}` : ''}`
        : String(value)
    }
    case 'enum':
    case 'momentary':
      return typeof value === 'string' ? optionText(value) : String(value)
    case 'flags':
      return flags.value.length > 0 ? flags.value.map(optionText).join(', ') : t('schema.noFlags')
    default:
      return typeof value === 'string' ? value : JSON.stringify(value)
  }
})
const jsonText = computed(() =>
  props.modelValue === undefined ? t('schema.noValue') : JSON.stringify(props.modelValue, null, 2),
)
</script>

<template>
  <div
    :class="cn('grid gap-2', props.class)"
    data-slot="schema-field"
    :data-type="schema.type"
    :data-invalid="error ? 'true' : undefined"
  >
    <!-- bool: the label beside a switch -->
    <div v-if="schema.type === 'bool'" class="flex items-center justify-between gap-4">
      <Label :id="labelId" :for="id" :class="cn(hideLabel && 'sr-only')">
        {{ label }}<span v-if="required" aria-hidden="true" class="text-destructive">*</span>
      </Label>
      <Switch
        :id="id"
        :model-value="modelValue === true"
        :disabled="!editable"
        :aria-invalid="error ? 'true' : undefined"
        :aria-describedby="describedBy"
        @update:model-value="(checked: boolean) => change(checked === true)"
      />
    </div>

    <template v-else>
      <Label
        v-if="schema.type !== 'flags'"
        :id="labelId"
        :for="schema.type === 'json' || schema.type === 'momentary' || readonly ? undefined : id"
        :class="cn(hideLabel && 'sr-only')"
      >
        {{ label }}<span v-if="required" aria-hidden="true" class="text-destructive">*</span>
      </Label>

      <!-- Read-only values (not json/flags, which have their own read-only form) -->
      <p
        v-if="readonly && schema.type !== 'json' && schema.type !== 'flags'"
        :aria-labelledby="labelId"
        class="text-sm font-medium tabular-nums"
        data-slot="schema-value"
      >
        {{ readText }}
      </p>

      <!-- number -->
      <div v-else-if="schema.type === 'number'" class="grid gap-3">
        <div class="flex items-center gap-2">
          <Input
            :id="id"
            :model-value="numberText"
            type="text"
            inputmode="decimal"
            autocomplete="off"
            :disabled="!editable"
            :aria-invalid="error ? 'true' : undefined"
            :aria-describedby="describedBy"
            class="tabular-nums"
            @update:model-value="onNumberInput"
            @change="onNumberChange"
            @keydown.enter="onNumberChange"
          />
          <span v-if="bounds?.unit" class="shrink-0 text-sm text-muted-foreground">{{ bounds.unit }}</span>
        </div>
        <Slider
          v-if="sliderRange"
          :model-value="sliderValue"
          :min="sliderRange.min"
          :max="sliderRange.max"
          :step="sliderRange.step"
          :disabled="!editable"
          :aria-labelledby="labelId"
          @update:model-value="(values) => onSlide(values, false)"
          @value-commit="(values: number[]) => onSlide(values, true)"
        />
      </div>

      <!-- enum -->
      <Select
        v-else-if="schema.type === 'enum'"
        :model-value="typeof modelValue === 'string' ? modelValue : undefined"
        :disabled="!editable"
        @update:model-value="(value) => change(value)"
      >
        <SelectTrigger
          :id="id"
          class="w-full"
          :aria-invalid="error ? 'true' : undefined"
          :aria-describedby="describedBy"
        >
          <SelectValue :placeholder="t('schema.choose')" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem v-for="option in options" :key="option" :value="option">
            {{ optionText(option) }}
          </SelectItem>
        </SelectContent>
      </Select>

      <!-- string -->
      <Input
        v-else-if="schema.type === 'string'"
        :id="id"
        :model-value="typeof modelValue === 'string' ? modelValue : ''"
        :type="secret ? 'password' : 'text'"
        :autocomplete="secret ? 'new-password' : 'off'"
        :maxlength="maxLength"
        :disabled="!editable"
        :aria-invalid="error ? 'true' : undefined"
        :aria-describedby="describedBy"
        @update:model-value="(value) => change(String(value), false)"
        @change="emit('commit', modelValue)"
        @keydown.enter="emit('commit', modelValue)"
      />

      <!-- flags -->
      <fieldset
        v-else-if="schema.type === 'flags'"
        class="grid gap-3"
        :aria-describedby="describedBy"
        :aria-invalid="error ? 'true' : undefined"
      >
        <legend :id="labelId" :class="cn('mb-2 text-sm leading-none font-medium', hideLabel && 'sr-only')">
          {{ label }}<span v-if="required" aria-hidden="true" class="text-destructive">*</span>
        </legend>
        <p v-if="readonly" class="text-sm font-medium">{{ readText }}</p>
        <div v-for="flag in options" v-else :key="flag" class="flex items-center gap-3">
          <Checkbox
            :id="`${id}-${flag}`"
            :model-value="flags.includes(flag)"
            :disabled="!editable"
            @update:model-value="(checked) => toggleFlag(flag, checked === true)"
          />
          <Label :for="`${id}-${flag}`" class="font-normal">{{ optionText(flag) }}</Label>
        </div>
      </fieldset>

      <!-- json (always read-only) -->
      <pre
        v-else-if="schema.type === 'json'"
        :aria-labelledby="labelId"
        tabindex="0"
        class="max-h-48 overflow-auto rounded-md border bg-muted/50 p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap"
        data-slot="schema-value"
        >{{ jsonText }}</pre>

      <!-- momentary: the last event, and the ones it can send -->
      <div v-else-if="schema.type === 'momentary'" class="grid gap-2" :aria-labelledby="labelId" role="group">
        <p class="text-sm font-medium" data-slot="schema-value">{{ readText }}</p>
        <div v-if="options.length > 0" class="flex flex-wrap gap-1.5">
          <Badge v-for="event in options" :key="event" variant="outline" class="text-muted-foreground">
            {{ optionText(event) }}
          </Badge>
        </div>
      </div>
    </template>

    <p v-if="help" :id="helpId" class="text-sm text-muted-foreground">{{ help }}</p>
    <p v-if="error" :id="errorId" class="text-sm text-destructive" role="alert">{{ error }}</p>
  </div>
</template>
