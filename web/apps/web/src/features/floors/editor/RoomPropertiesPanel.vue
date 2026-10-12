<script setup lang="ts">
import { Button, Input, Label, ToggleGroup, ToggleGroupItem, cn, toast } from '@controlhome/ui'
import { ChevronLeft, CircleAlert, Trash } from '@lucide/vue'
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import KeyHint from '../../../components/KeyHint.vue'
import { NAME_MAX_LENGTH } from '../../../components/floorplan/geometry'
import { formatArea, formatDimensions, roomAreaM2 } from '../../../components/floorplan/measure'
import { ROOM_COLOR_TOKENS, roomColorClasses } from '../../../components/floorplan/roomColors'
import { ROOM_ICONS, roomIcon } from '../../../components/floorplan/roomIcons'
import { parseDecimal } from '../../../lib/validation'
import type { EditorMessage } from './draft'
import { rectangleOf, resizeRectangle, sizeOf, type RectangleCm } from './tools/dimensions'
import { moveVertex, removeVertex } from './tools/selectTool'
import { useEditor } from './useEditorState'

/**
 * The selected room: name (with quick names), its size (typed in metres for rectangular rooms, and the selected
 * corner's position), color, icon, problems, and delete.
 */
const { t, locale } = useI18n()
const state = useEditor()
const id = useId()

const room = computed(() => state.selectedRoom.value!)
const errors = computed(() => state.errors.value.rooms[room.value.key] ?? {})
const area = computed(() => formatArea(roomAreaM2(room.value.polygon), locale.value))
const size = computed(() => sizeOf(room.value.polygon))
const dimensions = computed(() => formatDimensions(size.value.width, size.value.height, locale.value))
const colors = computed(() => roomColorClasses(room.value.colorToken))
const Icon = computed(() => roomIcon(room.value.icon))
const message = (error: EditorMessage | undefined) => (error ? t(error.key, error.params ?? {}) : '')

const PRESETS = [
  { id: 'living', icon: 'sofa' },
  { id: 'kitchen', icon: 'cooking-pot' },
  { id: 'bedroom', icon: 'bed-double' },
  { id: 'bathroom', icon: 'bath' },
  { id: 'office', icon: 'briefcase' },
  { id: 'dining', icon: 'utensils' },
  { id: 'laundry', icon: 'washing-machine' },
  { id: 'hall', icon: 'door-open' },
  { id: 'garage', icon: 'car' },
  { id: 'garden', icon: 'trees' },
] as const

function rename(value: string | number): void {
  const key = room.value.key
  state.updateRoom(key, { name: String(value) }, 'floors.editor.history.rename', `name:${key}`)
}

function usePreset(preset: (typeof PRESETS)[number]): void {
  state.updateRoom(
    room.value.key,
    { name: t(`floors.editor.presets.${preset.id}`), icon: preset.icon },
    'floors.editor.history.rename',
  )
}

function setColor(value: unknown): void {
  if (typeof value !== 'string' || !value || value === room.value.colorToken) return
  state.updateRoom(room.value.key, { colorToken: value }, 'floors.editor.history.color')
}

function setIcon(value: unknown): void {
  if (typeof value !== 'string' || !value || value === room.value.icon) return
  state.updateRoom(room.value.key, { icon: value }, 'floors.editor.history.icon')
}

// --- Typed sizes (metres) ------------------------------------------------------------------------------------------
const metres = (cm: number) =>
  new Intl.NumberFormat(locale.value, { maximumFractionDigits: 2, useGrouping: false }).format(cm / 100)

/** A typed number of metres in centimetres, or null (empty, garbage). */
function centimetres(text: string): number | null {
  const parsed = parseDecimal(text)
  return parsed === null || !Number.isFinite(parsed) ? null : Math.round(parsed * 100)
}

/** Shows the current value again after a typed value that was not taken. */
function restore(event: Event, value: number): void {
  ;(event.target as HTMLInputElement).value = metres(value)
}

const rectangle = computed(() => rectangleOf(room.value.polygon))
const rectangleFields = computed(() => {
  const rect = rectangle.value
  if (!rect) return []
  return [
    { field: 'width' as const, label: t('floors.editor.room.width'), value: rect.width },
    { field: 'height' as const, label: t('floors.editor.room.depth'), value: rect.height },
    { field: 'x' as const, label: t('floors.editor.room.fromLeft'), value: rect.x },
    { field: 'y' as const, label: t('floors.editor.room.fromTop'), value: rect.y },
  ]
})

function setRectangle(field: keyof RectangleCm, event: Event): void {
  const rect = rectangle.value
  const cm = centimetres((event.target as HTMLInputElement).value)
  if (!rect) return
  if (cm === null || ((field === 'width' || field === 'height') && cm <= 0)) {
    restore(event, rect[field])
    return
  }
  if (cm === Math.round(rect[field])) return
  const polygon = resizeRectangle(room.value.polygon, { ...rect, [field]: cm })
  if (!polygon) return
  const resized = field === 'width' || field === 'height'
  state.updateRoom(
    room.value.key,
    { polygon },
    resized ? 'floors.editor.history.resizeRoom' : 'floors.editor.history.moveRoom',
  )
}

// --- The selected corner -------------------------------------------------------------------------------------------
const vertex = computed(() => state.selection.value.vertex)
const corner = computed(() => (vertex.value === null ? null : (room.value.polygon[vertex.value] ?? null)))

function setCorner(axis: 'x' | 'y', event: Event): void {
  const index = vertex.value
  const point = corner.value
  if (index === null || !point) return
  const cm = centimetres((event.target as HTMLInputElement).value)
  if (cm === null) {
    restore(event, point[axis])
    return
  }
  if (cm === Math.round(point[axis])) return
  state.updateRoom(
    room.value.key,
    { polygon: moveVertex(room.value.polygon, index, { ...point, [axis]: cm }) },
    'floors.editor.history.moveCorner',
  )
  state.select(room.value.key, index)
}

function removeCorner(): void {
  if (vertex.value === null) return
  const polygon = removeVertex(room.value.polygon, vertex.value)
  if (!polygon) {
    toast.info(t('floors.editor.room.minCorners'))
    return
  }
  state.updateRoom(room.value.key, { polygon }, 'floors.editor.history.removeCorner')
  state.select(room.value.key)
}
</script>

<template>
  <section class="grid gap-5" :aria-label="t('floors.editor.room.title')">
    <div class="grid gap-3">
      <Button variant="ghost" size="sm" class="-ml-2 w-fit text-muted-foreground" @click="state.select(null)">
        <ChevronLeft />
        {{ t('floors.editor.room.back') }}
      </Button>
      <div class="flex items-center gap-3">
        <span class="shrink-0 rounded-xl bg-card" aria-hidden="true">
          <span :class="cn('flex size-11 items-center justify-center rounded-xl border', colors.sample)">
            <component :is="Icon" class="size-5" />
          </span>
        </span>
        <div class="grid min-w-0 gap-0.5">
          <h2 class="truncate text-lg leading-tight font-semibold">
            {{ room.name.trim() || t('floors.editor.room.untitled') }}
          </h2>
          <p class="text-sm text-muted-foreground tabular-nums" data-testid="room-summary">
            {{ area }} · {{ dimensions }}
          </p>
        </div>
      </div>
    </div>

    <p
      v-if="errors.polygon || errors.other"
      role="alert"
      class="flex gap-2 rounded-lg border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
    >
      <CircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      <span>{{ message(errors.polygon ?? errors.other) }}</span>
    </p>

    <div class="grid gap-2">
      <Label :for="`${id}-name`">{{ t('floors.editor.room.name') }}</Label>
      <Input
        :id="`${id}-name`"
        :model-value="room.name"
        :maxlength="NAME_MAX_LENGTH + 20"
        autocomplete="off"
        :aria-invalid="errors.name ? true : undefined"
        :aria-describedby="errors.name ? `${id}-name-error` : undefined"
        @update:model-value="rename"
      />
      <p v-if="errors.name" :id="`${id}-name-error`" class="text-sm text-destructive">
        {{ message(errors.name) }}
      </p>
      <div class="flex flex-wrap gap-1.5 pt-1" role="group" :aria-label="t('floors.editor.room.presets')">
        <Button
          v-for="preset in PRESETS"
          :key="preset.id"
          variant="outline"
          size="xs"
          class="rounded-full font-normal"
          @click="usePreset(preset)"
        >
          {{ t(`floors.editor.presets.${preset.id}`) }}
        </Button>
      </div>
    </div>

    <!-- Rectangular rooms: width × depth and position, typed in metres -->
    <fieldset v-if="rectangleFields.length > 0" class="grid gap-2">
      <legend class="mb-2 text-sm font-medium">{{ t('floors.editor.room.size') }}</legend>
      <div class="grid grid-cols-2 gap-x-3 gap-y-3">
        <div v-for="entry in rectangleFields" :key="entry.field" class="grid gap-1.5">
          <Label :for="`${id}-${entry.field}`" class="text-xs font-normal text-muted-foreground">
            {{ entry.label }}
          </Label>
          <Input
            :id="`${id}-${entry.field}`"
            :model-value="metres(entry.value)"
            inputmode="decimal"
            autocomplete="off"
            class="tabular-nums"
            @change="setRectangle(entry.field, $event)"
            @keydown.enter="setRectangle(entry.field, $event)"
          />
        </div>
      </div>
    </fieldset>

    <!-- A selected corner: where it is, typed in metres -->
    <fieldset v-if="vertex !== null && corner" class="grid gap-2 rounded-lg border bg-muted/40 p-3">
      <legend class="sr-only">{{ t('floors.editor.room.cornerSelected', { n: vertex + 1 }) }}</legend>
      <div class="flex items-center justify-between gap-3 text-sm" aria-hidden="true">
        <span class="font-medium">{{ t('floors.editor.room.cornerSelected', { n: vertex + 1 }) }}</span>
      </div>
      <div class="grid grid-cols-2 gap-3">
        <div v-for="axis in ['x', 'y'] as const" :key="axis" class="grid gap-1.5">
          <Label :for="`${id}-corner-${axis}`" class="text-xs font-normal text-muted-foreground">
            {{ axis === 'x' ? t('floors.editor.room.fromLeft') : t('floors.editor.room.fromTop') }}
          </Label>
          <Input
            :id="`${id}-corner-${axis}`"
            :model-value="metres(corner[axis])"
            inputmode="decimal"
            autocomplete="off"
            class="tabular-nums"
            @change="setCorner(axis, $event)"
            @keydown.enter="setCorner(axis, $event)"
          />
        </div>
      </div>
      <Button variant="ghost" size="sm" class="-mx-1 w-fit" @click="removeCorner">
        {{ t('floors.editor.room.removeCorner') }}
      </Button>
    </fieldset>

    <div class="grid gap-2">
      <span :id="`${id}-color`" class="text-sm font-medium">{{ t('floors.editor.room.color') }}</span>
      <!-- Each swatch is the room fill as it looks on the plan, outlined in the solid color -->
      <ToggleGroup
        type="single"
        :model-value="room.colorToken"
        :aria-labelledby="`${id}-color`"
        class="flex w-full flex-wrap gap-2"
        :spacing="2"
        @update:model-value="setColor"
      >
        <ToggleGroupItem
          v-for="token in ROOM_COLOR_TOKENS"
          :key="token"
          :value="token"
          :aria-label="t(`floors.editor.colors.${token}`)"
          class="size-control min-w-control rounded-full p-0 hover:bg-transparent data-[state=on]:bg-transparent"
        >
          <span
            :class="
              cn(
                'size-full rounded-full bg-card ring-offset-2 ring-offset-background transition-shadow',
                room.colorToken === token && 'ring-2 ring-foreground',
              )
            "
            aria-hidden="true"
          >
            <span :class="cn('block size-full rounded-full border-2', roomColorClasses(token).sample)" />
          </span>
        </ToggleGroupItem>
      </ToggleGroup>
    </div>

    <div class="grid gap-2">
      <span :id="`${id}-icon`" class="text-sm font-medium">{{ t('floors.editor.room.icon') }}</span>
      <ToggleGroup
        type="single"
        :model-value="room.icon"
        :aria-labelledby="`${id}-icon`"
        class="grid w-full grid-cols-7 gap-1"
        :spacing="1"
        @update:model-value="setIcon"
      >
        <ToggleGroupItem
          v-for="entry in ROOM_ICONS"
          :key="entry.key"
          :value="entry.key"
          :aria-label="t(`floors.editor.icons.${entry.key}`)"
          class="aspect-square h-auto min-w-0! rounded-lg p-0 data-[state=on]:bg-primary data-[state=on]:text-primary-foreground"
        >
          <component :is="entry.icon" class="size-4.5" />
        </ToggleGroupItem>
      </ToggleGroup>
    </div>

    <Button
      variant="outline"
      class="justify-between text-destructive hover:bg-destructive/10 hover:text-destructive"
      @click="state.deleteRoom(room.key)"
    >
      <span class="flex items-center gap-2">
        <Trash />
        {{ t('floors.editor.room.delete') }}
      </span>
      <KeyHint keys="Del" />
    </Button>
  </section>
</template>
