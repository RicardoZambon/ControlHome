<script setup lang="ts">
import { uploadFile } from '@controlhome/api-client'
import { Button, Label, Slider } from '@controlhome/ui'
import { ImageUp, Move, Ruler, Trash } from '@lucide/vue'
import { computed, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import { formatLength } from '../../../components/floorplan/measure'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { fitBackground } from './tools/backgroundTool'
import { useEditor } from './useEditorState'

/**
 * The traced plan image: upload (PNG/JPEG/WebP, re-encoded by the server), opacity, scale (calibrated with two points),
 * position (dragged with the Background tool), replace and remove. Every change is part of the draft (undoable, saved
 * with the floor).
 */
const { t, locale } = useI18n()
const state = useEditor()
const problem = useProblem()
const opacityId = useId()

const MAX_BYTES = 10 * 1024 * 1024
const ACCEPT = 'image/png,image/jpeg,image/webp'

const input = ref<HTMLInputElement | null>(null)
const uploading = ref(false)
const error = ref('')

const background = computed(() => state.draft.value.background)
const size = computed(() => state.backgroundSize.value)
const thumbnail = computed(() =>
  background.value ? `/api/files/${encodeURIComponent(background.value.fileId)}` : null,
)
const scaleText = computed(() =>
  background.value
    ? t('floors.editor.background.scaleValue', {
        length: formatLength(background.value.cmPerPx, locale.value),
      })
    : '',
)
const opacity = computed(() => Math.round((background.value?.opacity ?? 0) * 100))

function pick(): void {
  input.value?.click()
}

async function onFile(event: Event): Promise<void> {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  target.value = ''
  if (!file) return
  error.value = ''
  if (!ACCEPT.split(',').includes(file.type)) {
    error.value = t('errors.files.unsupported_type')
    return
  }
  if (file.size > MAX_BYTES) {
    error.value = t('errors.files.too_large')
    return
  }
  uploading.value = true
  try {
    const stored = unwrap(await uploadFile({ body: { file, purpose: 'floor-background' } }))
    const floor = state.draft.value.floor
    state.setBackground(
      fitBackground(stored.id, { widthPx: stored.widthPx, heightPx: stored.heightPx }, floor),
      'floors.editor.history.background',
    )
    state.backgroundSize.value = { widthPx: stored.widthPx, heightPx: stored.heightPx }
  } catch (failure) {
    error.value = problem.message(failure)
  } finally {
    uploading.value = false
  }
}

function setOpacity(value: number[] | undefined): void {
  const current = background.value
  const next = value?.[0]
  if (!current || next === undefined) return
  state.setBackground({ ...current, opacity: next / 100 }, 'floors.editor.history.opacity', 'opacity')
}

function remove(): void {
  state.setBackground(null, 'floors.editor.history.removeBackground')
}
</script>

<template>
  <section class="grid gap-5" :aria-label="t('floors.editor.background.title')">
    <div class="grid gap-1">
      <h2 class="leading-tight font-semibold">{{ t('floors.editor.background.title') }}</h2>
      <p class="text-sm text-muted-foreground">{{ t('floors.editor.background.description') }}</p>
    </div>

    <input
      ref="input"
      type="file"
      :accept="ACCEPT"
      class="sr-only"
      tabindex="-1"
      :aria-label="t('floors.editor.background.upload')"
      data-testid="background-file"
      @change="onFile"
    />

    <FormAlert v-if="error" :message="error" />

    <template v-if="!background">
      <button
        type="button"
        class="group grid place-items-center gap-2 rounded-xl border-2 border-dashed bg-muted/30 px-4 py-8 text-center transition-colors outline-none hover:border-ring/50 hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
        :disabled="uploading"
        @click="pick"
      >
        <span class="flex size-11 items-center justify-center rounded-full bg-background shadow-xs">
          <ImageUp class="size-5 text-primary" aria-hidden="true" />
        </span>
        <span class="font-medium">
          {{ uploading ? t('floors.editor.background.uploading') : t('floors.editor.background.upload') }}
        </span>
        <span class="text-xs text-muted-foreground">{{ t('floors.editor.background.formats') }}</span>
      </button>
    </template>

    <template v-else>
      <div class="overflow-hidden rounded-xl border bg-muted/30">
        <img
          v-if="thumbnail"
          :src="thumbnail"
          alt=""
          class="aspect-video w-full object-contain dark:hue-rotate-180 dark:invert"
          :style="{ opacity: Math.max(background.opacity, 0.25) }"
        />
        <div class="flex items-center justify-between gap-2 border-t px-3 py-2 text-xs text-muted-foreground">
          <span class="tabular-nums">
            {{
              size ? t('floors.editor.background.size', { width: size.widthPx, height: size.heightPx }) : '…'
            }}
          </span>
          <span class="tabular-nums" data-testid="background-scale">{{ scaleText }}</span>
        </div>
      </div>

      <div class="grid gap-3">
        <div class="flex items-center justify-between">
          <Label :id="opacityId">{{ t('floors.editor.background.opacity') }}</Label>
          <span class="text-sm text-muted-foreground tabular-nums">{{ opacity }}%</span>
        </div>
        <Slider
          :model-value="[opacity]"
          :min="5"
          :max="100"
          :step="5"
          :aria-labelledby="opacityId"
          @update:model-value="setOpacity"
        />
      </div>

      <div class="grid gap-2">
        <Button
          :variant="state.tool.value === 'calibrate' ? 'default' : 'outline'"
          class="justify-start"
          @click="state.setTool('calibrate')"
        >
          <Ruler />
          {{ t('floors.editor.background.calibrate') }}
        </Button>
        <p class="text-xs text-muted-foreground">{{ t('floors.editor.background.calibrateHelp') }}</p>
        <Button
          :variant="state.tool.value === 'background' ? 'default' : 'outline'"
          class="justify-start"
          @click="state.setTool('background')"
        >
          <Move />
          {{ t('floors.editor.background.move') }}
        </Button>
      </div>

      <div class="flex flex-wrap gap-2 border-t pt-4">
        <Button variant="outline" size="sm" :loading="uploading" @click="pick">
          <ImageUp />
          {{ uploading ? t('floors.editor.background.uploading') : t('floors.editor.background.replace') }}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          class="text-destructive hover:bg-destructive/10 hover:text-destructive"
          @click="remove"
        >
          <Trash />
          {{ t('floors.editor.background.remove') }}
        </Button>
      </div>
    </template>
  </section>
</template>
