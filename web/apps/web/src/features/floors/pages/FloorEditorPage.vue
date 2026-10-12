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
import { ArrowLeft, ImageUp, Pentagon, RefreshCw, SlidersHorizontal, Trash2 } from '@lucide/vue'
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
import { formatArea, formatLength, roomAreaM2 } from '../../../components/floorplan/measure'
import { roomColorClasses } from '../../../components/floorplan/roomColors'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import CalibrationDialog from '../editor/CalibrationDialog.vue'
import EditorOverlay from '../editor/EditorOverlay.vue'
import EditorSidePanel from '../editor/EditorSidePanel.vue'
import EditorToolbar from '../editor/EditorToolbar.vue'
import { commandFor, isTypingTarget } from '../editor/shortcuts'
import { calibrateBackground } from '../editor/tools/calibrateTool'
import { removeLastDrawPoint } from '../editor/tools/drawTool'
import { clampMove, removeVertex, translatePolygon } from '../editor/tools/selectTool'
import { useEditorPointer } from '../editor/useEditorPointer'
import { provideEditor, useEditorState } from '../editor/useEditorState'

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const problem = useProblem()

const state = useEditorState({ roomBaseName: () => t('floors.editor.roomBase') })
provideEditor(state)

const isLarge = useMediaQuery('(min-width: 1024px)')
const coarse = useMediaQuery('(pointer: coarse)')
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

// --- Canvas and pointer -------------------------------------------------------------------------------------------------
const canvas = ref<InstanceType<typeof FloorPlanCanvas> | null>(null)
const spaceDown = ref(false)
const calibrationOpen = ref(false)
const calibrationPoints = ref<{ a: Point; b: Point } | null>(null)
const calibrationError = ref('')

const pointer = useEditorPointer(state, {
  scale: () => canvas.value?.scale ?? 1,
  coarse: () => coarse.value,
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
      if (count === 0) return t(coarse.value ? 'floors.editor.hints.drawShort' : 'floors.editor.hints.draw')
      return t(coarse.value ? 'floors.editor.hints.drawNextShort' : 'floors.editor.hints.drawNext', { count })
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
      return t(coarse.value ? 'floors.editor.hints.selectShort' : 'floors.editor.hints.select')
  }
})
const coordinates = computed(() => {
  const cursor = pointer.cursor.value
  if (!cursor) return ''
  const metres = (cm: number) =>
    new Intl.NumberFormat(locale.value, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
      cm / 100,
    )
  return t('floors.editor.coordinates', { x: metres(cursor.x), y: metres(cursor.y) })
})

const showEmpty = computed(
  () => !loading.value && state.rooms.value.length === 0 && state.tool.value === 'select',
)

// --- Side panel / sheet ----------------------------------------------------------------------------------------------
const sheetOpen = ref(false)
watch(isLarge, (large) => {
  if (large) sheetOpen.value = false
})

function traceImage(): void {
  state.setTool('background')
  if (!isLarge.value) sheetOpen.value = true
}

const selected = computed(() => state.selectedRoom.value)
const selectedArea = computed(() =>
  selected.value ? formatArea(roomAreaM2(selected.value.polygon), locale.value) : '',
)

// --- Saving, leaving --------------------------------------------------------------------------------------------------
const conflictOpen = ref(false)
const leaveOpen = ref(false)
let pendingLeave: RouteLocationRaw | null = null
let allowLeave = false

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
    state.drawing.value = removeLastDrawPoint(state.drawing.value)
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
    class="flex h-[calc(100dvh-8.25rem-env(safe-area-inset-bottom))] min-h-[32rem] flex-col gap-3 md:h-[calc(100dvh-6.25rem)]"
  >
    <!-- Header: where we are, save state, Cancel / Save -->
    <header class="flex flex-wrap items-end gap-x-4 gap-y-2">
      <div class="min-w-0 flex-1">
        <RouterLink
          :to="viewRoute"
          class="inline-flex items-center gap-1.5 rounded-sm text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ArrowLeft class="size-3.5" aria-hidden="true" />
          {{ floorName ? t('floors.editor.back', { name: floorName }) : t('floors.view.back') }}
        </RouterLink>
        <h1 class="truncate font-serif text-2xl font-medium tracking-tight md:text-3xl">
          <template v-if="floorName">
            <!-- Phones: the floor's name is enough next to Cancel and Save. -->
            <span class="sm:hidden">{{ floorName }}</span>
            <span class="hidden sm:inline">{{ t('floors.editor.heading', { name: floorName }) }}</span>
          </template>
          <template v-else>{{ t('floors.editor.title') }}</template>
        </h1>
      </div>
      <div class="flex items-center gap-2">
        <span
          v-if="!loading && !loadError"
          class="mr-1 hidden items-center gap-2 text-sm text-muted-foreground sm:flex"
          aria-live="polite"
        >
          <span
            :class="cn('size-2 rounded-full', state.isDirty.value ? 'bg-state-warn' : 'bg-state-on')"
            aria-hidden="true"
          />
          {{ state.isDirty.value ? t('floors.editor.unsaved') : t('floors.editor.saved') }}
        </span>
        <Button variant="outline" :disabled="state.saving.value" @click="cancel">
          {{ t('common.cancel') }}
        </Button>
        <Button :loading="state.saving.value" :disabled="loading || !!loadError" @click="save">
          {{ state.saving.value ? t('floors.editor.saving') : t('floors.editor.save') }}
          <KeyHint :keys="`${mod}+S`" class="ml-1 hidden lg:inline-flex" />
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

    <div v-else class="flex min-h-0 flex-1 gap-3">
      <div
        class="relative -mx-3 min-w-0 flex-1 overflow-hidden border-y shadow-sm sm:mx-0 sm:rounded-2xl sm:border"
      >
        <FloorPlanCanvas
          ref="canvas"
          class="size-full"
          mode="edit"
          :floor="plan"
          :selected-room-id="state.tool.value === 'select' ? state.selection.value.roomKey : null"
          :hover-room-id="state.hoverRoomKey.value"
          :invalid-room-ids="state.invalidRoomKeys.value"
          :handler="pointer.handler"
          :force-pan="spaceDown"
          :cursor-class="spaceDown ? 'cursor-grab' : pointer.cursorClass.value"
          @background-loaded="state.backgroundSize.value = $event"
          @room-click="(_room, key) => state.select(key)"
        >
          <template #overlay="{ scale }">
            <EditorOverlay :scale="scale" :pointer="pointer" :coarse="coarse" />
          </template>
          <template #controls>
            <div
              class="pointer-events-none absolute inset-x-2 top-2 flex flex-col items-start gap-2 sm:inset-x-3 sm:top-3"
            >
              <div class="pointer-events-auto max-w-full">
                <EditorToolbar :show-properties="!isLarge" @properties="sheetOpen = true" />
              </div>

              <!-- Phones and tablets: the selected room, with a way into its properties -->
              <div
                v-if="!isLarge && selected && state.tool.value === 'select'"
                class="pointer-events-auto flex max-w-full items-center gap-2 rounded-xl border bg-background/95 py-1 pr-1 pl-3 shadow-md backdrop-blur-md"
              >
                <span
                  :class="cn('size-2.5 shrink-0 rounded-full', roomColorClasses(selected.colorToken).swatch)"
                  aria-hidden="true"
                />
                <span class="min-w-0 truncate text-sm font-medium">{{ selected.name }}</span>
                <span class="shrink-0 text-xs text-muted-foreground tabular-nums">{{ selectedArea }}</span>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  :aria-label="t('floors.editor.properties')"
                  @click="sheetOpen = true"
                >
                  <SlidersHorizontal />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  class="text-destructive hover:bg-destructive/10 hover:text-destructive"
                  :aria-label="t('floors.editor.room.delete')"
                  @click="state.deleteRoom(selected.key)"
                >
                  <Trash2 />
                </Button>
              </div>

              <p
                v-else-if="!isLarge && state.tool.value !== 'select'"
                class="max-w-full rounded-lg bg-background/90 px-3 py-1.5 text-xs text-muted-foreground shadow-sm backdrop-blur-md"
              >
                {{ hint }}
              </p>
            </div>

            <!-- Desktop hint bar -->
            <p
              v-if="isLarge"
              class="pointer-events-none absolute bottom-3 left-1/2 flex w-max max-w-[calc(100%-15rem)] -translate-x-1/2 items-center gap-3 rounded-xl border bg-background/90 px-4 py-1.5 text-xs leading-snug text-muted-foreground shadow-sm backdrop-blur-md"
              aria-live="polite"
            >
              <span class="line-clamp-2">{{ hint }}</span>
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
                <span class="flex size-12 items-center justify-center rounded-2xl bg-primary/10 text-primary">
                  <Pentagon class="size-6" aria-hidden="true" />
                </span>
                <h2 class="font-serif text-xl font-medium">{{ t('floors.editor.empty.title') }}</h2>
                <p class="text-sm text-muted-foreground">{{ t('floors.editor.empty.description') }}</p>
                <div class="flex flex-wrap justify-center gap-2 pt-1">
                  <Button @click="state.setTool('draw')">
                    <Pentagon />
                    {{ t('floors.editor.empty.start') }}
                    <KeyHint keys="R" class="ml-1" />
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
