<script setup lang="ts">
import {
  Button,
  Separator,
  ToggleGroup,
  ToggleGroupItem,
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
  cn,
} from '@controlhome/ui'
import { MousePointer2, Pentagon, Redo2, Undo2 } from '@lucide/vue'
import { computed, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import KeyHint from '../../../components/KeyHint.vue'
import { TOOL_KEYS } from './shortcuts'
import { useEditor, type EditorTool } from './useEditorState'

/**
 * The floating tool bar on the canvas: the two everyday tools (Select, Draw room), labelled when there is room, and
 * undo/redo. The background image's tools (move, calibrate) are one-time setup steps: they live in the Image tab.
 */
defineProps<{ labels?: boolean }>()

const { t } = useI18n()
const state = useEditor()

/** "Ctrl" or "⌘" in shortcut hints. */
const mod = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘' : 'Ctrl'

const tools = computed<{ id: EditorTool; icon: Component; label: string; short: string }[]>(() => [
  {
    id: 'select',
    icon: MousePointer2,
    label: t('floors.editor.tools.select'),
    short: t('floors.editor.tools.selectShort'),
  },
  {
    id: 'draw',
    icon: Pentagon,
    label: t('floors.editor.tools.draw'),
    short: t('floors.editor.tools.drawShort'),
  },
])

function onTool(value: unknown): void {
  if (typeof value === 'string' && value) state.setTool(value as EditorTool)
}

const undoTip = computed(() =>
  state.undoLabel.value
    ? t('floors.editor.undoAction', { action: t(state.undoLabel.value) })
    : t('floors.editor.undo'),
)
const redoTip = computed(() =>
  state.redoLabel.value
    ? t('floors.editor.redoAction', { action: t(state.redoLabel.value) })
    : t('floors.editor.redo'),
)
</script>

<template>
  <TooltipProvider :delay-duration="300">
    <div
      role="toolbar"
      :aria-label="t('floors.editor.tools.label')"
      class="flex max-w-full items-center gap-0.5 overflow-x-auto rounded-xl border bg-background/95 p-1 shadow-md backdrop-blur-md"
    >
      <ToggleGroup
        type="single"
        :model-value="state.tool.value"
        class="gap-0.5"
        :aria-label="t('floors.editor.tools.label')"
        @update:model-value="onTool"
      >
        <Tooltip v-for="tool in tools" :key="tool.id">
          <TooltipTrigger as-child>
            <ToggleGroupItem
              :value="tool.id"
              :aria-label="tool.label"
              :class="
                cn(
                  'h-control rounded-lg aria-pressed:bg-primary aria-pressed:text-primary-foreground aria-pressed:hover:bg-primary aria-pressed:hover:text-primary-foreground',
                  labels ? 'gap-2 px-3' : 'w-control px-0',
                )
              "
            >
              <component :is="tool.icon" class="size-4.5" />
              <span v-if="labels" aria-hidden="true">{{ tool.short }}</span>
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent side="bottom" class="flex items-center gap-2">
            {{ tool.label }}
            <KeyHint :keys="TOOL_KEYS[tool.id]" />
          </TooltipContent>
        </Tooltip>
      </ToggleGroup>

      <Separator orientation="vertical" class="mx-0.5 data-[orientation=vertical]:h-6" />

      <Tooltip>
        <TooltipTrigger as-child>
          <Button
            variant="ghost"
            size="icon"
            class="rounded-lg"
            :aria-label="t('floors.editor.undo')"
            :disabled="!state.canUndo.value"
            @click="state.undo()"
          >
            <Undo2 class="size-4.5" />
          </Button>
        </TooltipTrigger>
        <TooltipContent side="bottom" class="flex items-center gap-2">
          {{ undoTip }}
          <KeyHint :keys="`${mod}+Z`" />
        </TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger as-child>
          <Button
            variant="ghost"
            size="icon"
            class="rounded-lg"
            :aria-label="t('floors.editor.redo')"
            :disabled="!state.canRedo.value"
            @click="state.redo()"
          >
            <Redo2 class="size-4.5" />
          </Button>
        </TooltipTrigger>
        <TooltipContent side="bottom" class="flex items-center gap-2">
          {{ redoTip }}
          <KeyHint :keys="`${mod}+Y`" />
        </TooltipContent>
      </Tooltip>
    </div>
  </TooltipProvider>
</template>
