<script setup lang="ts">
import { getFloor } from '@controlhome/api-client'
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  Button,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  Skeleton,
  cn,
  toast,
} from '@controlhome/ui'
import { Check, ChevronUp, ImageUp, Layers, Pentagon, RefreshCw, Undo2, X } from '@lucide/vue'
import { useEventListener, useMediaQuery } from '@vueuse/core'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  onBeforeRouteLeave,
  onBeforeRouteUpdate,
  useRoute,
  useRouter,
  type RouteLocationNormalized,
  type RouteLocationRaw,
} from 'vue-router'
import KeyHint from '../../../components/KeyHint.vue'
import FloorPlanCanvas from '../../../components/floorplan/FloorPlanCanvas.vue'
import type { Point } from '../../../components/floorplan/geometry'
import { formatArea, formatDimensions, formatLength, roomAreaM2 } from '../../../components/floorplan/measure'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { roomIcon } from '../../../components/floorplan/roomIcons'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import CalibrationDialog from '../editor/CalibrationDialog.vue'
import EditorOverlay from '../editor/EditorOverlay.vue'
import EditorSidePanel from '../editor/EditorSidePanel.vue'
import EditorToolbar from '../editor/EditorToolbar.vue'
import { commandFor, isTypingTarget } from '../editor/shortcuts'
import { calibrateBackground } from '../editor/tools/calibrateTool'
import { sizeOf } from '../editor/tools/dimensions'
import { canCloseDraw, removeLastDrawPoint } from '../editor/tools/drawTool'
import { clampMove, removeVertex, translatePolygon } from '../editor/tools/selectTool'
import { useEditorPointer } from '../editor/useEditorPointer'
import { provideEditor, useEditorState, type DeletedRoom } from '../editor/useEditorState'

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const problem = useProblem()

/** Every deleted room (Delete key, the panel) gets a toast that can bring it back. */
function offerUndo(deleted: DeletedRoom): void {
  toast(t('floors.editor.room.deleted', { name: deleted.name.trim() || t('floors.editor.room.untitled') }), {
    action: { label: t('floors.editor.undo'), onClick: () => void deleted.undo() },
  })
}

const state = useEditorState({ roomBaseName: () => t('floors.editor.roomBase'), onRoomDeleted: offerUndo })
provideEditor(state)

const isLarge = useMediaQuery('(min-width: 1024px)')
const coarse = useMediaQuery('(pointer: coarse)')
/** Touch screens, and phone-size windows: bigger handles, tap wording, on-screen drawing actions. */
const touchUi = computed(() => coarse.value || !isLarge.value)
const mod = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘' : 'Ctrl'

// --- Loading ----------------------------------------------------------------------------------------------------------
const floorId = computed(() => Number(route.params.id))
const loading = ref(true)
const loadError = ref<unknown>(null)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    state.load(unwrap(await getFloor({ path: { id: floorId.value } })))
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
watch(floorId, load, { immediate: true })

const viewRoute = computed<RouteLocationRaw>(() => ({ name: 'floor', params: { id: floorId.value } }))
const floorName = computed(() => state.server.value?.name ?? '')

usePageTrail(() =>
  floorName.value
    ? [
        { label: t('floors.title'), to: { name: 'floors' } },
        { label: floorName.value, to: viewRoute.value },
        { label: t('floors.editor.title') },
      ]
    : null,
)

// --- Canvas and pointer -------------------------------------------------------------------------------------------------
const canvas = ref<InstanceType<typeof FloorPlanCanvas> | null>(null)
const spaceDown = ref(false)
const calibrationOpen = ref(false)
const calibrationPoints = ref<{ a: Point; b: Point } | null>(null)
const calibrationError = ref('')

const pointer = useEditorPointer(state, {
  scale: () => canvas.value?.scale ?? 1,
  coarse: () => touchUi.value,
  onCalibrationPoints: (a, b) => {
    calibrationPoints.value = { a, b }
    calibrationError.value = ''
    calibrationOpen.value = true
  },
})

const plan = computed(() => ({
  name: state.draft.value.floor.name,
  widthCm: state.draft.value.floor.widthCm,
  heightCm: state.draft.value.floor.heightCm,
  gridCm: state.draft.value.floor.gridCm,
  background: state.draft.value.background,
  rooms: state.rooms.value,
}))

const measuredCm = computed(() => {
  const points = calibrationPoints.value
  return points ? Math.hypot(points.b.x - points.a.x, points.b.y - points.a.y) : 0
})

function applyCalibration(realCm: number): void {
  const background = state.draft.value.background
  const points = calibrationPoints.value
  if (!background || !points) return
  const calibrated = calibrateBackground(background, points.a, points.b, realCm)
  if (!calibrated) {
    calibrationError.value = t('floors.editor.calibration.outOfRange')
    return
  }
  state.setBackground(calibrated, 'floors.editor.history.calibrate')
  calibrationOpen.value = false
  state.setTool('select')
  toast.success(
    t('floors.editor.calibration.done', {
      scale: t('floors.editor.background.scaleValue', {
        length: formatLength(calibrated.cmPerPx, locale.value),
      }),
    }),
  )
}

watch(calibrationOpen, (open) => {
  if (!open) {
    state.calibration.value = { a: null, b: null }
    calibrationPoints.value = null
  }
})

// --- Hints ---------------------------------------------------------------------------------------------------------------
const hint = computed(() => {
  switch (state.tool.value) {
    case 'draw': {
      const count = state.drawing.value.points.length
      if (count === 0) return t(touchUi.value ? 'floors.editor.hints.drawShort' : 'floors.editor.hints.draw')
      return t(touchUi.value ? 'floors.editor.hints.drawNextShort' : 'floors.editor.hints.drawNext', {
        count,
      })
    }
    case 'calibrate':
      return state.calibration.value.a
        ? t('floors.editor.hints.calibrateSecond')
        : t('floors.editor.hints.calibrate')
    case 'background':
      return state.draft.value.background
        ? t('floors.editor.hints.background')
        : t('floors.editor.hints.noBackground')
    default:
      return t(touchUi.value ? 'floors.editor.hints.selectShort' : 'floors.editor.hints.select')
  }
})
/** Where the pointer is, while placing points (drawing, calibrating). */
const coordinates = computed(() => {
  const cursor = pointer.cursor.value
  if (!cursor || (state.tool.value !== 'draw' && state.tool.value !== 'calibrate')) return ''
  const metres = (cm: number) =>
    new Intl.NumberFormat(locale.value, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
      cm / 100,
    )
  return t('floors.editor.coordinates', { x: metres(cursor.x), y: metres(cursor.y) })
})

const showEmpty = computed(
  () => !loading.value && state.rooms.value.length === 0 && state.tool.value === 'select',
)

// --- Drawing on a touch screen: Finish, Undo corner, Cancel ---------------------------------------------------------
const drawing = computed(() => state.tool.value === 'draw')
const corners = computed(() => state.drawing.value.points.length)
const canFinish = computed(() => canCloseDraw(state.drawing.value))

function finishDrawing(): void {
  pointer.finishDrawing()
}

function undoCorner(): void {
  state.drawing.value = removeLastDrawPoint(state.drawing.value)
}

function cancelDrawing(): void {
  pointer.cancelTool()
  state.setTool('select')
}

// --- Side panel / sheet ----------------------------------------------------------------------------------------------
const sheetOpen = ref(false)
watch(isLarge, (large) => {
  if (large) sheetOpen.value = false
})

function traceImage(): void {
  state.setTool('background')
  if (!isLarge.value) sheetOpen.value = true
}

const selected = computed(() => (state.tool.value === 'select' ? state.selectedRoom.value : null))
const selectedSummary = computed(() => {
  const room = selected.value
  if (!room) return ''
  const size = sizeOf(room.polygon)
  return `${formatArea(roomAreaM2(room.polygon), locale.value)} · ${formatDimensions(size.width, size.height, locale.value)}`
})
const floorSummary = computed(() => {
  const rooms = state.rooms.value
  const count = t('floors.roomCount', rooms.length)
  if (rooms.length === 0) return count
  const total = rooms.reduce((sum, room) => sum + roomAreaM2(room.polygon), 0)
  return `${count} · ${formatArea(total, locale.value)}`
})

// --- Saving, leaving --------------------------------------------------------------------------------------------------
const conflictOpen = ref(false)
const leaveOpen = ref(false)
let pendingLeave: RouteLocationRaw | null = null
let allowLeave = false

const dirty = computed(() => !loading.value && !loadError.value && state.isDirty.value)

async function save(): Promise<void> {
  const outcome = await state.save()
  switch (outcome.kind) {
    case 'saved':
      toast.success(t('floors.toast.saved'))
      break
    case 'nothing':
      toast.info(t('floors.editor.nothingToSave'))
      break
    case 'invalid': {
      toast.error(t('floors.editor.fixProblems'))
      const first = state.rooms.value.find((room) => room.key in state.errors.value.rooms)
      if (first) state.select(first.key)
      else state.panelTab.value = 'floor'
      if (!isLarge.value) sheetOpen.value = true
      break
    }
    case 'conflict':
      conflictOpen.value = true
      break
    case 'failed':
      toast.error(`${t('floors.editor.saveFailed')} ${problem.message(outcome.error)}`)
      break
  }
}

async function reloadAfterConflict(): Promise<void> {
  conflictOpen.value = false
  await load()
}

function cancel(): void {
  if (state.isDirty.value) {
    pendingLeave = viewRoute.value
    leaveOpen.value = true
    return
  }
  void router.push(viewRoute.value)
}

function discardAndLeave(): void {
  leaveOpen.value = false
  allowLeave = true
  if (pendingLeave) void router.push(pendingLeave)
}

/** Leaving (or opening another floor's editor) with unsaved changes asks first. */
function guardLeave(to: RouteLocationNormalized): boolean {
  // The session ended: the sign-in page must open (the draft cannot be saved anyway).
  if (allowLeave || !state.isDirty.value || to.name === 'login') {
    allowLeave = false
    return true
  }
  pendingLeave = to.fullPath
  leaveOpen.value = true
  return false
}
onBeforeRouteLeave(guardLeave)
onBeforeRouteUpdate(guardLeave)

useEventListener(window, 'beforeunload', (event: BeforeUnloadEvent) => {
  if (!state.isDirty.value) return
  event.preventDefault()
  event.returnValue = ''
})

// --- Keyboard --------------------------------------------------------------------------------------------------------
const dialogOpen = computed(
  () => calibrationOpen.value || conflictOpen.value || leaveOpen.value || sheetOpen.value,
)

/** Buttons and toggles keep their own Enter and Space. */
const activatesWithKey = (target: EventTarget | null) =>
  target instanceof Element &&
  target.closest('button, a, [role="button"], [role="radio"], [role="tab"]') !== null

function deleteSelection(): void {
  if (state.tool.value === 'draw' && state.drawing.value.points.length > 0) {
    undoCorner()
    return
  }
  const room = state.selectedRoom.value
  if (!room) return
  const vertex = state.selection.value.vertex
  if (vertex === null) {
    state.deleteRoom(room.key)
    return
  }
  const polygon = removeVertex(room.polygon, vertex)
  if (!polygon) {
    toast.info(t('floors.editor.room.minCorners'))
    return
  }
  state.updateRoom(room.key, { polygon }, 'floors.editor.history.removeCorner')
  state.select(room.key)
}

function nudge(dx: number, dy: number, fine: boolean): void {
  const room = state.selectedRoom.value
  if (!room || state.tool.value !== 'select') return
  const step = fine ? 1 : state.draft.value.floor.gridCm
  const { widthCm, heightCm } = state.draft.value.floor
  const move = clampMove(room.polygon, dx * step, dy * step, widthCm, heightCm)
  state.updateRoom(
    room.key,
    { polygon: translatePolygon(room.polygon, move.dx, move.dy) },
    'floors.editor.history.nudge',
    `nudge:${room.key}`,
  )
}

function onKeydown(event: KeyboardEvent): void {
  if (loading.value || loadError.value || dialogOpen.value || event.defaultPrevented) return
  if (isTypingTarget(event.target)) {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault()
      void save()
    }
    return
  }
  if (event.key === ' ' && !activatesWithKey(event.target)) {
    event.preventDefault()
    spaceDown.value = true
    return
  }
  const command = commandFor(event)
  if (!command) return
  if (command.kind === 'finish' && activatesWithKey(event.target)) return
  event.preventDefault()
  switch (command.kind) {
    case 'tool':
      state.setTool(command.tool)
      break
    case 'undo':
      state.undo()
      break
    case 'redo':
      state.redo()
      break
    case 'save':
      void save()
      break
    case 'delete':
      deleteSelection()
      break
    case 'finish':
      pointer.finishDrawing()
      break
    case 'cancel':
      if (pointer.cancelTool()) break
      if (state.selection.value.vertex !== null) state.select(state.selection.value.roomKey)
      else if (state.selection.value.roomKey) state.select(null)
      else if (state.tool.value !== 'select') state.setTool('select')
      break
    case 'zoomIn':
      canvas.value?.zoomIn()
      break
    case 'zoomOut':
      canvas.value?.zoomOut()
      break
    case 'fit':
      canvas.value?.fit()
      break
    case 'nudge':
      nudge(command.dx, command.dy, command.fine)
      break
  }
}

useEventListener(window, 'keydown', onKeydown)
useEventListener(window, 'keyup', (event: KeyboardEvent) => {
  if (event.key === ' ') spaceDown.value = false
})
useEventListener(window, 'blur', () => (spaceDown.value = false))
</script>

<template>
  <div
    class="flex h-[calc(100dvh-5rem-env(safe-area-inset-bottom))] min-h-[32rem] flex-col gap-3 md:h-[calc(100dvh-6.25rem)]"
  >
    <!-- Header: the floor, its save state, Cancel / Save -->
    <header class="flex flex-wrap items-end gap-x-4 gap-y-2">
      <div class="grid min-w-0 flex-1 gap-1">
        <p class="eyebrow">{{ t('floors.editor.eyebrow') }}</p>
        <h1 class="truncate font-serif text-2xl font-medium tracking-tight md:text-3xl">
          {{ floorName || t('floors.editor.title') }}
        </h1>
      </div>
      <div class="flex items-center gap-2">
        <span
          v-if="!loading && !loadError"
          class="mr-1 hidden items-center gap-2 text-sm text-muted-foreground sm:flex"
          aria-live="polite"
          data-testid="save-state"
        >
          <span v-if="dirty" class="size-2 rounded-full bg-state-warn" aria-hidden="true" />
          {{ dirty ? t('floors.editor.unsaved') : t('floors.editor.clean') }}
        </span>
        <Button variant="outline" :disabled="state.saving.value" @click="cancel">
          {{ t('common.cancel') }}
        </Button>
        <Button
          class="relative"
          :loading="state.saving.value"
          :disabled="loading || !!loadError || (!dirty && !state.saving.value)"
          @click="save"
        >
          {{ state.saving.value ? t('floors.editor.saving') : t('floors.editor.save') }}
          <KeyHint :keys="`${mod}+S`" class="ml-1 hidden lg:inline-flex" />
          <!-- Phones have no room for the status: a dot on Save says there is something to save. -->
          <template v-if="dirty">
            <span
              class="absolute -top-1 -right-1 size-3 rounded-full bg-state-warn ring-2 ring-background sm:hidden"
              aria-hidden="true"
            />
            <span class="sr-only sm:hidden">({{ t('floors.editor.unsaved') }})</span>
          </template>
        </Button>
      </div>
    </header>

    <div
      v-if="loadError"
      class="grid flex-1 place-items-center rounded-2xl border border-dashed p-8 text-center"
    >
      <div class="grid max-w-sm justify-items-center gap-3">
        <p class="text-muted-foreground">{{ problem.message(loadError) }}</p>
        <Button variant="outline" @click="load">
          <RefreshCw />
          {{ t('common.retry') }}
        </Button>
      </div>
    </div>

    <Skeleton v-else-if="loading" class="flex-1 rounded-2xl" />

    <template v-else>
      <div class="flex min-h-0 flex-1 gap-3">
        <div
          class="relative -mx-3 min-w-0 flex-1 overflow-hidden border-y shadow-sm sm:mx-0 sm:rounded-2xl sm:border"
        >
          <FloorPlanCanvas
            ref="canvas"
            class="size-full"
            mode="edit"
            :floor="plan"
            :selected-room-id="state.tool.value === 'select' ? state.selection.value.roomKey : null"
            :hover-room-id="state.tool.value === 'select' ? state.hoverRoomKey.value : null"
            :invalid-room-ids="state.invalidRoomKeys.value"
            :handler="pointer.handler"
            :force-pan="spaceDown"
            :cursor-class="spaceDown ? 'cursor-grab' : pointer.cursorClass.value"
            @background-loaded="state.backgroundSize.value = $event"
            @room-click="(_room, key) => state.select(key)"
          >
            <template #overlay="{ scale }">
              <EditorOverlay :scale="scale" :pointer="pointer" :coarse="touchUi" />
            </template>
            <template #controls>
              <div
                class="pointer-events-none absolute inset-x-2 top-2 flex flex-col items-start gap-2 sm:inset-x-3 sm:top-3"
              >
                <div class="pointer-events-auto max-w-full">
                  <EditorToolbar :labels="isLarge" />
                </div>

                <!-- Phones and tablets: what the active tool expects -->
                <p
                  v-if="!isLarge && state.tool.value !== 'select'"
                  class="max-w-full rounded-lg bg-background/90 px-3 py-1.5 text-xs text-muted-foreground shadow-sm backdrop-blur-md"
                  aria-live="polite"
                >
                  {{ hint }}
                </p>
              </div>

              <!-- A touch screen beside the side panel: the drawing actions float over the plan -->
              <div v-if="isLarge && coarse && drawing" class="absolute bottom-3 left-1/2 -translate-x-1/2">
                <div
                  role="toolbar"
                  :aria-label="t('floors.editor.drawing.label')"
                  class="flex items-center gap-1.5 rounded-xl border bg-background/95 p-1.5 shadow-md backdrop-blur-md"
                >
                  <Button size="sm" :disabled="!canFinish" @click="finishDrawing">
                    <Check />
                    {{ t('floors.editor.drawing.finish', { count: corners }) }}
                  </Button>
                  <Button size="sm" variant="outline" :disabled="corners === 0" @click="undoCorner">
                    <Undo2 />
                    {{ t('floors.editor.drawing.undoCorner') }}
                  </Button>
                  <Button size="sm" variant="ghost" @click="cancelDrawing">
                    {{ t('floors.editor.drawing.cancel') }}
                  </Button>
                </div>
              </div>

              <!-- Desktop hint bar: one line, with the cursor's position while drawing or calibrating -->
              <p
                v-else-if="isLarge"
                class="pointer-events-none absolute bottom-3 left-1/2 flex w-max max-w-[calc(100%-17rem)] -translate-x-1/2 items-center gap-3 rounded-lg border bg-background/90 px-3 py-1.5 text-xs leading-5 text-muted-foreground shadow-sm backdrop-blur-md"
                aria-live="polite"
              >
                <span class="truncate" :title="hint">{{ hint }}</span>
                <span
                  v-if="coordinates"
                  class="shrink-0 border-l pl-3 font-medium text-foreground tabular-nums"
                >
                  {{ coordinates }}
                </span>
              </p>

              <!-- Empty floor: how to start -->
              <div v-if="showEmpty" class="pointer-events-none absolute inset-0 grid place-items-center p-6">
                <div
                  class="pointer-events-auto grid max-w-sm justify-items-center gap-3 rounded-2xl border bg-background/95 p-6 text-center shadow-lg backdrop-blur-md motion-safe:animate-in motion-safe:fade-in motion-safe:zoom-in-95"
                >
                  <span
                    class="flex size-12 items-center justify-center rounded-2xl bg-primary/10 text-primary"
                  >
                    <Pentagon class="size-6" aria-hidden="true" />
                  </span>
                  <h2 class="font-serif text-xl font-medium">{{ t('floors.editor.empty.title') }}</h2>
                  <p class="text-sm text-muted-foreground">{{ t('floors.editor.empty.description') }}</p>
                  <div class="flex flex-wrap justify-center gap-2 pt-1">
                    <Button @click="state.setTool('draw')">
                      <Pentagon />
                      {{ t('floors.editor.empty.start') }}
                      <KeyHint keys="R" class="ml-1 max-lg:hidden" />
                    </Button>
                    <Button variant="outline" @click="traceImage">
                      <ImageUp />
                      {{ t('floors.editor.empty.trace') }}
                    </Button>
                  </div>
                </div>
              </div>
            </template>
          </FloorPlanCanvas>
        </div>

        <aside
          v-if="isLarge"
          class="flex w-80 shrink-0 flex-col overflow-hidden rounded-2xl border bg-card shadow-sm xl:w-88"
          :aria-label="t('floors.editor.properties')"
        >
          <EditorSidePanel />
        </aside>
      </div>

      <!--
        Phones and tablets: the panel as a sheet that always peeks out below the plan. While drawing it holds the
        drawing actions (touch screens have no Enter, Backspace or Esc).
      -->
      <div
        v-if="!isLarge"
        class="-mx-3 -mb-3 flex min-h-16 items-center gap-2 rounded-t-2xl border-x border-t bg-card px-3 pt-2.5 pb-[calc(0.625rem+env(safe-area-inset-bottom))] shadow-[0_-4px_12px_-6px_var(--shadow-color)] sm:mx-0"
        data-testid="editor-peek"
      >
        <div
          v-if="drawing"
          role="toolbar"
          :aria-label="t('floors.editor.drawing.label')"
          class="flex w-full items-center gap-2"
        >
          <Button class="min-w-0 flex-1" :disabled="!canFinish" @click="finishDrawing">
            <Check />
            <span class="truncate">{{ t('floors.editor.drawing.finish', { count: corners }) }}</span>
          </Button>
          <Button
            variant="outline"
            size="icon"
            :disabled="corners === 0"
            :aria-label="t('floors.editor.drawing.undoCorner')"
            @click="undoCorner"
          >
            <Undo2 />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            :aria-label="t('floors.editor.drawing.cancel')"
            @click="cancelDrawing"
          >
            <X />
          </Button>
        </div>

        <template v-else>
          <button
            type="button"
            class="flex min-h-11 min-w-0 flex-1 items-center gap-3 rounded-lg px-1 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-haspopup="dialog"
            @click="sheetOpen = true"
          >
            <span v-if="selected" class="shrink-0 rounded-lg bg-card" aria-hidden="true">
              <span
                :class="
                  cn(
                    'flex size-9 items-center justify-center rounded-lg border',
                    roomColorClasses(selected.colorToken).sample,
                  )
                "
              >
                <component :is="roomIcon(selected.icon)" class="size-4" />
              </span>
            </span>
            <span
              v-else
              class="flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground"
              aria-hidden="true"
            >
              <Layers class="size-4" />
            </span>
            <span class="grid min-w-0 flex-1">
              <template v-if="selected">
                <span class="truncate text-sm font-medium">
                  {{ selected.name.trim() || t('floors.editor.room.untitled') }}
                </span>
                <span class="truncate text-xs text-muted-foreground tabular-nums">{{ selectedSummary }}</span>
              </template>
              <template v-else>
                <span class="truncate text-sm font-medium">{{ t('floors.editor.tabs.rooms') }}</span>
                <span class="truncate text-xs text-muted-foreground tabular-nums">{{ floorSummary }}</span>
              </template>
            </span>
            <span class="sr-only">{{ t('floors.editor.openPanel') }}</span>
            <ChevronUp class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          </button>
          <Button v-if="!selected" variant="outline" class="shrink-0" @click="state.setTool('draw')">
            <Pentagon />
            {{ t('floors.editor.drawing.start') }}
          </Button>
        </template>
      </div>
    </template>

    <Sheet v-if="!isLarge" v-model:open="sheetOpen">
      <SheetContent
        side="bottom"
        :close-label="t('common.close')"
        class="flex h-[80dvh] flex-col gap-0 rounded-t-2xl p-0"
      >
        <SheetHeader class="sr-only">
          <SheetTitle>{{ t('floors.editor.properties') }}</SheetTitle>
          <SheetDescription>{{ t('floors.editor.propertiesDescription') }}</SheetDescription>
        </SheetHeader>
        <div class="flex min-h-0 flex-1 flex-col pt-10">
          <EditorSidePanel />
        </div>
      </SheetContent>
    </Sheet>

    <CalibrationDialog
      v-model:open="calibrationOpen"
      :measured-cm="measuredCm"
      :error="calibrationError"
      @apply="applyCalibration"
    />

    <AlertDialog v-model:open="conflictOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('floors.editor.conflict.title') }}
          </AlertDialogTitle>
          <AlertDialogDescription>{{ t('floors.editor.conflict.description') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ t('floors.editor.conflict.keep') }}</AlertDialogCancel>
          <Button @click="reloadAfterConflict">
            <RefreshCw />
            {{ t('floors.editor.conflict.reload') }}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>

    <AlertDialog v-model:open="leaveOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('floors.editor.leave.title') }}
          </AlertDialogTitle>
          <AlertDialogDescription>{{ t('floors.editor.leave.description') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{{ t('floors.editor.leave.stay') }}</AlertDialogCancel>
          <Button variant="destructive" @click="discardAndLeave">{{
            t('floors.editor.leave.discard')
          }}</Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
