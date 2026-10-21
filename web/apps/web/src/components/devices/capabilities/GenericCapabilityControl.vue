<script setup lang="ts">
import type { AttributeDefinitionDto, CommandDefinitionDto, ValueSchema } from '@controlhome/api-client'
import { Button } from '@controlhome/ui'
import { KeyRound } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import { isSafetyCritical } from '../../../composables/useCommand'
import SchemaField from '../../schema/SchemaField.vue'
import SchemaForm from '../../schema/SchemaForm.vue'
import { defaultValueFor, validateValue, type SchemaFieldDefinition } from '../../schema/schema'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * The fallback control for any capability without its own (and for `generic` vendor properties): every attribute as a
 * `<SchemaField>` — editable when it is writable and has a setter command, read-only otherwise — and every other
 * command as a button, or as a small form when it takes arguments.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const JSON_SCHEMA: ValueSchema = { type: 'json' }

/** The command that sets an attribute on its own (its only required argument is bound to it). */
function setterOf(attribute: AttributeDefinitionDto): CommandDefinitionDto | undefined {
  return control.definition.value?.commands.find((command) => {
    const required = command.arguments.filter((argument) => argument.required)
    return required.length === 1 && (required[0]!.attribute ?? required[0]!.key) === attribute.key
  })
}

interface AttributeRow {
  key: string
  schema: ValueSchema
  setter?: CommandDefinitionDto
}

const attributes = computed<AttributeRow[]>(() => {
  const definition = control.definition.value
  if (definition) {
    return definition.attributes.map((attribute) => ({
      key: attribute.key,
      schema: attribute.schema,
      setter: attribute.writable ? setterOf(attribute) : undefined,
    }))
  }
  // No definition known: show what the endpoint reported, read-only.
  return props.capability.attributes.map((attribute) => ({ key: attribute.key, schema: JSON_SCHEMA }))
})

const setterNames = computed(
  () => new Set(attributes.value.flatMap((row) => (row.setter ? [row.setter.name] : []))),
)
const otherCommands = computed(() =>
  (control.definition.value?.commands ?? []).filter((command) => !setterNames.value.has(command.name)),
)
const buttonCommands = computed(() => otherCommands.value.filter((command) => command.arguments.length === 0))
const formCommands = computed(() => otherCommands.value.filter((command) => command.arguments.length > 0))

const editable = (row: AttributeRow) =>
  control.enabled.value && !!row.setter && row.schema.type !== 'json' && row.schema.type !== 'momentary'

const errors = reactive<Record<string, string | undefined>>({})

function commitAttribute(row: AttributeRow, value: unknown): void {
  const setter = row.setter
  if (!setter) return
  const argument = setter.arguments.find((entry) => entry.required)!
  const problem = validateValue(row.schema, value, {
    required: true,
    constraint: control.constraint(row.key),
  })
  errors[row.key] = problem ? t('validation.invalid') : undefined
  if (problem) return
  void control.send(setter.name, { [argument.key]: value })
}

// Forms of the commands that take arguments.
const formValues = ref<Record<string, Record<string, unknown>>>({})
function fieldsOf(command: CommandDefinitionDto): SchemaFieldDefinition[] {
  return command.arguments.map((argument) => ({
    key: argument.key,
    label: control.attributeLabel(argument.key),
    schema: argument.schema,
    required: argument.required,
    constraint: control.constraint(argument.attribute ?? argument.key),
  }))
}
watch(
  formCommands,
  (commands) => {
    for (const command of commands) {
      formValues.value[command.name] ??= Object.fromEntries(
        command.arguments
          .filter((argument) => argument.required)
          .map((argument) => [
            argument.key,
            defaultValueFor(argument.schema, control.constraint(argument.attribute ?? argument.key)),
          ]),
      )
    }
  },
  { immediate: true },
)

const needsPin = (command: CommandDefinitionDto) => isSafetyCritical(command, props.device.deviceClass)
</script>

<template>
  <div class="grid gap-6" data-slot="generic-control">
    <p v-if="attributes.length === 0 && otherCommands.length === 0" class="text-sm text-muted-foreground">
      {{ t('device.generic.empty') }}
    </p>

    <div v-if="attributes.length > 0" class="grid gap-x-6 gap-y-5 sm:grid-cols-2">
      <SchemaField
        v-for="row in attributes"
        :key="row.key"
        :schema="row.schema"
        :model-value="control.state.plain(row.key)"
        :label="control.attributeLabel(row.key)"
        :readonly="!editable(row)"
        :constraint="control.constraint(row.key)"
        :error="errors[row.key]"
        :option-label="(value: string) => control.valueLabel(row.key, value)"
        :class="row.schema.type === 'json' ? 'sm:col-span-2' : undefined"
        @commit="(value) => commitAttribute(row, value)"
      />
    </div>

    <template v-if="control.canControl.value">
      <div v-if="buttonCommands.length > 0" class="flex flex-wrap gap-2">
        <Button
          v-for="command in buttonCommands"
          :key="command.name"
          variant="outline"
          :disabled="!control.enabled.value"
          :loading="control.pending(command.name)"
          @click="control.send(command.name)"
        >
          <KeyRound v-if="needsPin(command)" aria-hidden="true" />
          {{ control.commandLabel(command.name) }}
        </Button>
      </div>

      <section
        v-for="command in formCommands"
        :key="command.name"
        class="grid gap-4 rounded-xl border bg-background/60 p-4"
        :aria-label="control.commandLabel(command.name)"
      >
        <SchemaForm
          :fields="fieldsOf(command)"
          :model-value="formValues[command.name] ?? {}"
          :disabled="!control.enabled.value"
          :busy="control.pending(command.name)"
          :submit-label="control.commandLabel(command.name)"
          :option-label="(field, value) => control.valueLabel(field.key, value)"
          @update:model-value="(value) => (formValues[command.name] = value)"
          @submit="(value) => control.send(command.name, value)"
        />
      </section>
    </template>
  </div>
</template>
