<script setup lang="ts">
import { Button, Tabs, TabsContent, TabsList, TabsTrigger, cn } from '@controlhome/ui'
import { CircleAlert, Pentagon } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import KeyHint from '../../../components/KeyHint.vue'
import { formatArea, roomAreaM2 } from '../../../components/floorplan/measure'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import BackgroundPanel from './BackgroundPanel.vue'
import FloorPropertiesForm from './FloorPropertiesForm.vue'
import RoomPropertiesPanel from './RoomPropertiesPanel.vue'
import type { EditorMessage } from './draft'
import { useEditor, type PanelTab } from './useEditorState'

/**
 * The editor's side panel (a sheet below lg): Rooms (the list, or the selected room's properties), Floor (its
 * properties) and Background (the traced image). After a save attempt that found problems it lists them at the top.
 */
const { t, locale } = useI18n()
const state = useEditor()

const rooms = computed(() =>
  state.rooms.value.map((room) => ({
    ...room,
    area: formatArea(roomAreaM2(room.polygon), locale.value),
    Icon: roomIcon(room.icon),
    colors: roomColorClasses(room.colorToken),
    invalid: room.key in state.errors.value.rooms,
  })),
)
const total = computed(() =>
  formatArea(
    state.rooms.value.reduce((sum, room) => sum + roomAreaM2(room.polygon), 0),
    locale.value,
  ),
)

const message = (error: EditorMessage | undefined) => (error ? t(error.key, error.params ?? {}) : '')

/** Problems to list after a save attempt: per room (first problem) and per floor field. */
const problems = computed(() => {
  if (!state.attemptedSave.value) return []
  const list: { id: string; label: string; text: string; go: () => void }[] = []
  for (const room of state.rooms.value) {
    const errors = state.errors.value.rooms[room.key]
    if (!errors) continue
    list.push({
      id: room.key,
      label: room.name.trim() || t('floors.editor.room.untitled'),
      text: message(errors.polygon ?? errors.name ?? errors.other),
      go: () => state.select(room.key),
    })
  }
  for (const [field, error] of Object.entries(state.errors.value.floor)) {
    list.push({
      id: `floor-${field}`,
      label: t('floors.editor.tabs.floor'),
      text: message(error),
      go: () => (state.panelTab.value = field === 'background' ? 'background' : 'floor'),
    })
  }
  return list
})

function onTab(value: string | number): void {
  state.panelTab.value = value as PanelTab
}
</script>

<template>
  <Tabs
    :model-value="state.panelTab.value"
    class="flex min-h-0 flex-1 flex-col gap-0"
    @update:model-value="onTab"
  >
    <div class="border-b p-3">
      <TabsList class="grid w-full grid-cols-3">
        <TabsTrigger value="rooms">{{ t('floors.editor.tabs.rooms') }}</TabsTrigger>
        <TabsTrigger value="floor">{{ t('floors.editor.tabs.floor') }}</TabsTrigger>
        <TabsTrigger value="background">{{ t('floors.editor.tabs.background') }}</TabsTrigger>
      </TabsList>
    </div>

    <div class="min-h-0 flex-1 overflow-y-auto p-4">
      <div
        v-if="problems.length > 0"
        role="alert"
        class="mb-5 grid gap-2 rounded-xl border border-destructive/40 bg-destructive/5 p-3"
      >
        <p class="flex items-center gap-2 text-sm font-medium text-destructive">
          <CircleAlert class="size-4 shrink-0" aria-hidden="true" />
          {{ t('floors.editor.problems', { count: problems.length }, problems.length) }}
        </p>
        <ul class="grid gap-1">
          <li v-for="problem in problems" :key="problem.id">
            <button
              type="button"
              class="w-full rounded-md px-2 py-1.5 text-left text-sm outline-none hover:bg-destructive/10 focus-visible:ring-2 focus-visible:ring-ring"
              @click="problem.go"
            >
              <span class="font-medium">{{ problem.label }}</span>
              <span class="block text-muted-foreground">{{ problem.text }}</span>
            </button>
          </li>
        </ul>
      </div>

      <TabsContent value="rooms" class="mt-0">
        <RoomPropertiesPanel v-if="state.selectedRoom.value" :key="state.selectedRoom.value.key" />
        <section v-else class="grid gap-4" :aria-label="t('floors.editor.tabs.rooms')">
          <div class="flex items-baseline justify-between gap-3">
            <h2 class="font-serif text-lg font-medium">{{ t('floors.editor.tabs.rooms') }}</h2>
            <span v-if="rooms.length > 0" class="text-sm text-muted-foreground tabular-nums">
              {{ t('floors.roomCount', rooms.length) }} · {{ total }}
            </span>
          </div>

          <ul v-if="rooms.length > 0" class="grid gap-1">
            <li v-for="room in rooms" :key="room.key">
              <button
                type="button"
                class="group flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring"
                @click="state.select(room.key)"
                @pointerenter="state.hoverRoomKey.value = room.key"
                @pointerleave="state.hoverRoomKey.value = null"
              >
                <span
                  :class="cn('flex size-9 shrink-0 items-center justify-center rounded-lg', room.colors.tint)"
                  aria-hidden="true"
                >
                  <component :is="room.Icon" class="size-4" />
                </span>
                <span class="grid min-w-0 flex-1">
                  <span class="truncate text-sm font-medium">
                    {{ room.name.trim() || t('floors.editor.room.untitled') }}
                  </span>
                  <span class="text-xs text-muted-foreground tabular-nums">{{ room.area }}</span>
                </span>
                <CircleAlert
                  v-if="room.invalid"
                  class="size-4 shrink-0 text-destructive"
                  :aria-label="t('floors.editor.rooms.invalid')"
                />
                <span :class="cn('size-2.5 shrink-0 rounded-full', room.colors.swatch)" aria-hidden="true" />
              </button>
            </li>
          </ul>
          <p v-else class="text-sm text-muted-foreground">{{ t('floors.editor.rooms.empty') }}</p>

          <Button variant="outline" class="justify-between" @click="state.setTool('draw')">
            <span class="flex items-center gap-2">
              <Pentagon />
              {{ t('floors.editor.rooms.draw') }}
            </span>
            <KeyHint keys="R" />
          </Button>
        </section>
      </TabsContent>

      <TabsContent value="floor" class="mt-0">
        <FloorPropertiesForm />
      </TabsContent>

      <TabsContent value="background" class="mt-0">
        <BackgroundPanel />
      </TabsContent>
    </div>
  </Tabs>
</template>
