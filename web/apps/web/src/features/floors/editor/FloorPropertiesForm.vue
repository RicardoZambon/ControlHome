<script setup lang="ts">
import { Input, Label } from '@controlhome/ui'
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { parseDecimal } from '../../../lib/validation'
import type { EditorMessage, FloorField } from './draft'
import { useEditor } from './useEditorState'

/**
 * The floor's own properties: name, level, outside size (in metres) and the snapping grid (cm). Numbers apply when the
 * field is left (or Enter), so half-typed values never resize the plan; the name applies as it is typed.
 */
const { t, locale } = useI18n()
const state = useEditor()
const id = useId()

const floor = computed(() => state.draft.value.floor)
const errors = computed(() => state.errors.value.floor)
const message = (error: EditorMessage | undefined) => (error ? t(error.key, error.params ?? {}) : '')

const format = (value: number, digits = 2) =>
  new Intl.NumberFormat(locale.value, { maximumFractionDigits: digits, useGrouping: false }).format(value)

function rename(value: string | number): void {
  state.updateFloor({ name: String(value) }, 'floor:name')
}

/** Applies a typed number (`scale`: 100 for metres → cm); garbage leaves the field showing the current value. */
function commit(field: 'level' | 'widthCm' | 'heightCm' | 'gridCm', text: string, scale = 1): void {
  const parsed = parseDecimal(text)
  if (parsed === null || !Number.isFinite(parsed)) return
  const value = Math.round(parsed * scale)
  if (value !== floor.value[field]) state.updateFloor({ [field]: value })
}

const fields = computed(() => [
  {
    field: 'level' as const,
    label: t('floors.form.level'),
    value: String(floor.value.level),
    scale: 1,
    help: t('floors.form.levelHelp'),
    inputmode: 'numeric' as const,
  },
  {
    field: 'widthCm' as const,
    label: t('floors.form.width'),
    value: format(floor.value.widthCm / 100),
    scale: 100,
    inputmode: 'decimal' as const,
  },
  {
    field: 'heightCm' as const,
    label: t('floors.form.height'),
    value: format(floor.value.heightCm / 100),
    scale: 100,
    help: t('floors.form.sizeHelp'),
    inputmode: 'decimal' as const,
  },
  {
    field: 'gridCm' as const,
    label: t('floors.editor.floor.grid'),
    value: String(floor.value.gridCm),
    scale: 1,
    help: t('floors.editor.floor.gridHelp'),
    inputmode: 'numeric' as const,
  },
])

const errorOf = (field: FloorField) => message(errors.value[field])
</script>

<template>
  <section class="grid gap-5" :aria-label="t('floors.editor.tabs.floor')">
    <div class="grid gap-2">
      <Label :for="`${id}-name`">{{ t('floors.form.name') }}</Label>
      <Input
        :id="`${id}-name`"
        :model-value="floor.name"
        autocomplete="off"
        :aria-invalid="errors.name ? true : undefined"
        :aria-describedby="errors.name ? `${id}-name-error` : undefined"
        @update:model-value="rename"
      />
      <p v-if="errors.name" :id="`${id}-name-error`" class="text-sm text-destructive">
        {{ errorOf('name') }}
      </p>
    </div>

    <div class="grid grid-cols-2 gap-x-3 gap-y-5">
      <div
        v-for="entry in fields"
        :key="entry.field"
        :class="entry.field === 'level' || entry.field === 'gridCm' ? 'col-span-2' : ''"
        class="grid content-start gap-2"
      >
        <Label :for="`${id}-${entry.field}`">{{ entry.label }}</Label>
        <Input
          :id="`${id}-${entry.field}`"
          :model-value="entry.value"
          :inputmode="entry.inputmode"
          autocomplete="off"
          class="tabular-nums"
          :aria-invalid="errors[entry.field] ? true : undefined"
          :aria-describedby="`${id}-${entry.field}-help`"
          @change="commit(entry.field, ($event.target as HTMLInputElement).value, entry.scale)"
          @keydown.enter="commit(entry.field, ($event.target as HTMLInputElement).value, entry.scale)"
        />
        <p
          :id="`${id}-${entry.field}-help`"
          :class="errors[entry.field] ? 'text-sm text-destructive' : 'text-xs text-muted-foreground'"
        >
          {{ errors[entry.field] ? errorOf(entry.field) : (entry.help ?? '') }}
        </p>
      </div>
    </div>
  </section>
</template>
