<script setup lang="ts">
import { Button, Input, Label, ToggleGroup, ToggleGroupItem, cn, toast } from '@controlhome/ui'
import { ChevronLeft, CircleAlert, Trash2 } from '@lucide/vue'
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import KeyHint from '../../../components/KeyHint.vue'
import { NAME_MAX_LENGTH } from '../../../components/floorplan/geometry'
import { formatArea, roomAreaM2 } from '../../../components/floorplan/measure'
import { ROOM_COLOR_TOKENS, roomColorClasses } from '../../../components/floorplan/roomColors'
import { ROOM_ICONS, roomIcon } from '../../../components/floorplan/roomIcons'
import type { EditorMessage } from './draft'
import { removeVertex } from './tools/selectTool'
import { useEditor } from './useEditorState'

/** The selected room: name (with quick names), color, icon, its size, problems, and delete. */
const { t, locale } = useI18n()
const state = useEditor()
const nameId = useId()
const nameErrorId = useId()

const room = computed(() => state.selectedRoom.value!)
const errors = computed(() => state.errors.value.rooms[room.value.key] ?? {})
const area = computed(() => formatArea(roomAreaM2(room.value.polygon), locale.value))
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

const vertex = computed(() => state.selection.value.vertex)

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
        <span
          :class="cn('flex size-11 shrink-0 items-center justify-center rounded-xl', colors.tint)"
          aria-hidden="true"
        >
          <component :is="Icon" class="size-5" />
        </span>
        <div class="grid min-w-0 gap-0.5">
          <h2 class="truncate font-serif text-xl leading-tight font-medium">
            {{ room.name.trim() || t('floors.editor.room.untitled') }}
          </h2>
          <p class="text-sm text-muted-foreground tabular-nums">
            {{ area }} · {{ t('floors.editor.room.corners', { count: room.polygon.length }) }}
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
      <Label :for="nameId">{{ t('floors.editor.room.name') }}</Label>
      <Input
        :id="nameId"
        :model-value="room.name"
        :maxlength="NAME_MAX_LENGTH + 20"
        autocomplete="off"
        :aria-invalid="errors.name ? true : undefined"
        :aria-describedby="errors.name ? nameErrorId : undefined"
        @update:model-value="rename"
      />
      <p v-if="errors.name" :id="nameErrorId" class="text-sm text-destructive">
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

    <div class="grid gap-2">
      <span :id="`${nameId}-color`" class="text-sm font-medium">{{ t('floors.editor.room.color') }}</span>
      <ToggleGroup
        type="single"
        :model-value="room.colorToken"
        :aria-labelledby="`${nameId}-color`"
        class="flex w-full flex-wrap gap-2"
        :spacing="2"
        @update:model-value="setColor"
      >
        <ToggleGroupItem
          v-for="token in ROOM_COLOR_TOKENS"
          :key="token"
          :value="token"
          :aria-label="t(`floors.editor.colors.${token}`)"
          class="size-8 min-w-0! rounded-full p-0 data-[state=on]:bg-transparent"
        >
          <span
            :class="
              cn(
                'size-full rounded-full ring-offset-2 ring-offset-background transition-shadow',
                roomColorClasses(token).swatch,
                room.colorToken === token && 'ring-2 ring-foreground',
              )
            "
            aria-hidden="true"
          />
        </ToggleGroupItem>
      </ToggleGroup>
    </div>

    <div class="grid gap-2">
      <span :id="`${nameId}-icon`" class="text-sm font-medium">{{ t('floors.editor.room.icon') }}</span>
      <ToggleGroup
        type="single"
        :model-value="room.icon"
        :aria-labelledby="`${nameId}-icon`"
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

    <div
      v-if="vertex !== null"
      class="flex items-center justify-between gap-3 rounded-lg border bg-muted/40 px-3 py-2 text-sm"
    >
      <span>{{ t('floors.editor.room.cornerSelected', { n: vertex + 1 }) }}</span>
      <Button variant="ghost" size="sm" @click="removeCorner">
        {{ t('floors.editor.room.removeCorner') }}
      </Button>
    </div>

    <Button
      variant="outline"
      class="justify-between text-destructive hover:bg-destructive/10 hover:text-destructive"
      @click="state.deleteRoom(room.key)"
    >
      <span class="flex items-center gap-2">
        <Trash2 />
        {{ t('floors.editor.room.delete') }}
      </span>
      <KeyHint keys="Del" />
    </Button>
  </section>
</template>
