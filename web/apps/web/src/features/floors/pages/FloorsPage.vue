<script setup lang="ts">
import { deleteFloor, type FloorDto } from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  Button,
  Dialog,
  DialogScrollContent,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Skeleton,
  toast,
} from '@controlhome/ui'
import { EllipsisVertical, PencilRuler, Plus, RefreshCw, Trash2 } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import FloorPlanArt from '../../../components/brand/FloorPlanArt.vue'
import FloorPreview from '../../../components/floorplan/FloorPreview.vue'
import { formatArea } from '../../../components/floorplan/measure'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'
import CreateFloorForm from '../components/CreateFloorForm.vue'
import { floorEyebrow, loadFloorsWithRooms, totalAreaM2 } from '../../../components/floorplan/floors'

const { t, locale } = useI18n()
const router = useRouter()
const session = useSessionStore()
const problem = useProblem()

const floors = ref<FloorDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    floors.value = await loadFloorsWithRooms()
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
onMounted(load)

const cards = computed(() =>
  floors.value.map((floor) => ({
    floor,
    level: floorEyebrow(floor, t),
    rooms: t('floors.roomCount', floor.rooms.length),
    area: floor.rooms.length > 0 ? formatArea(totalAreaM2(floor), locale.value) : null,
  })),
)

// --- Create ------------------------------------------------------------------------------------------------------------
const createOpen = ref(false)
const suggestedLevel = computed(() =>
  floors.value.length === 0 ? 0 : Math.max(...floors.value.map((floor) => floor.level)) + 1,
)
const suggestedName = computed(() => (floors.value.length === 0 ? t('floors.level.ground') : ''))

function onCreated(floor: FloorDto): void {
  createOpen.value = false
  toast.success(t('floors.toast.created', { name: floor.name }))
  void router.push({ name: 'floor-edit', params: { id: floor.id } })
}

// --- Delete ------------------------------------------------------------------------------------------------------------
const deleting = ref<FloorDto | null>(null)
const deleteOpen = ref(false)
const deleteBusy = ref(false)
const deleteError = ref('')

function askDelete(floor: FloorDto): void {
  deleting.value = floor
  deleteError.value = ''
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  const floor = deleting.value
  if (!floor) return
  deleteBusy.value = true
  deleteError.value = ''
  try {
    unwrap(await deleteFloor({ path: { id: floor.id } }))
    floors.value = floors.value.filter((entry) => entry.id !== floor.id)
    deleteOpen.value = false
    toast.success(t('floors.toast.deleted', { name: floor.name }))
  } catch (error) {
    deleteError.value = problem.message(error)
  } finally {
    deleteBusy.value = false
  }
}

function onDeleteOpenChange(open: boolean): void {
  deleteOpen.value = open || deleteBusy.value
}
</script>

<template>
  <div>
    <PageHeader
      :eyebrow="t('floors.eyebrow')"
      :title="t('floors.title')"
      :description="t('floors.description')"
    >
      <template v-if="session.isAdmin && floors.length > 0" #actions>
        <Button class="max-sm:w-full" @click="createOpen = true">
          <Plus />
          {{ t('floors.add') }}
        </Button>
      </template>
    </PageHeader>

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('floors.loadFailed') }} {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <ul
      v-else-if="loading"
      class="grid gap-5 sm:grid-cols-2 xl:grid-cols-3"
      :aria-label="t('floors.listLabel')"
    >
      <li v-for="index in 2" :key="index" class="overflow-hidden rounded-2xl border bg-card">
        <Skeleton class="aspect-4/3 w-full rounded-none" />
        <div class="grid gap-2 p-5">
          <Skeleton class="h-3 w-24" />
          <Skeleton class="h-6 w-40" />
        </div>
      </li>
    </ul>

    <!-- No floors yet -->
    <section
      v-else-if="floors.length === 0"
      class="relative grid overflow-hidden rounded-2xl border bg-card shadow-sm md:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]"
      aria-labelledby="floors-empty"
    >
      <div class="grid content-center gap-4 p-6 sm:p-10">
        <h2 id="floors-empty" class="font-serif text-3xl font-medium tracking-tight">
          {{ t('floors.empty.title') }}
        </h2>
        <p class="max-w-md text-muted-foreground">
          {{ session.isAdmin ? t('floors.empty.description') : t('floors.empty.member') }}
        </p>
        <div v-if="session.isAdmin">
          <Button size="lg" @click="createOpen = true">
            <Plus />
            {{ t('floors.empty.cta') }}
          </Button>
        </div>
      </div>
      <div class="border-t bg-muted/30 p-6 sm:p-10 md:border-t-0 md:border-l">
        <FloorPlanArt variant="outline" class="mx-auto max-w-md" />
      </div>
    </section>

    <ul v-else class="grid gap-5 sm:grid-cols-2 xl:grid-cols-3" :aria-label="t('floors.listLabel')">
      <li
        v-for="card in cards"
        :key="card.floor.id"
        class="group relative flex flex-col overflow-hidden rounded-2xl border bg-card shadow-xs transition-[border-color,box-shadow] has-[a:focus-visible]:ring-2 has-[a:focus-visible]:ring-ring has-[a:focus-visible]:ring-offset-2 has-[a:focus-visible]:ring-offset-background hover:border-ring/40 hover:shadow-md"
      >
        <FloorPreview :floor="card.floor" class="border-b" />
        <div class="flex items-start gap-3 p-5">
          <div class="grid min-w-0 flex-1 gap-1">
            <p class="eyebrow">{{ card.level }}</p>
            <h2 class="truncate font-serif text-xl font-medium">
              <RouterLink
                :to="{ name: 'floor', params: { id: card.floor.id } }"
                class="outline-none after:absolute after:inset-0 after:content-['']"
              >
                {{ card.floor.name }}
              </RouterLink>
            </h2>
            <p class="text-sm text-muted-foreground tabular-nums">
              {{ card.rooms }}<template v-if="card.area"> · {{ card.area }}</template>
            </p>
          </div>
          <DropdownMenu v-if="session.isAdmin">
            <DropdownMenuTrigger as-child>
              <Button
                variant="ghost"
                size="icon-sm"
                class="relative z-10 -mr-2 shrink-0"
                :aria-label="t('floors.actions', { name: card.floor.name })"
              >
                <EllipsisVertical />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem @select="router.push({ name: 'floor-edit', params: { id: card.floor.id } })">
                <PencilRuler />
                {{ t('floors.editPlan') }}
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive" @select="askDelete(card.floor)">
                <Trash2 />
                {{ t('common.delete') }}
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </li>
    </ul>

    <Dialog v-model:open="createOpen">
      <DialogScrollContent :close-label="t('common.close')" class="sm:max-w-md">
        <CreateFloorForm
          v-if="createOpen"
          :suggested-name="suggestedName"
          :suggested-level="suggestedLevel"
          @created="onCreated"
        />
      </DialogScrollContent>
    </Dialog>

    <AlertDialog :open="deleteOpen" @update:open="onDeleteOpenChange">
      <AlertDialogContent v-if="deleting">
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('floors.delete.title', { name: deleting.name }) }}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {{ t('floors.delete.description', deleting.rooms.length) }}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <FormAlert v-if="deleteError" :message="deleteError" />
        <AlertDialogFooter>
          <AlertDialogCancel :disabled="deleteBusy">{{ t('common.cancel') }}</AlertDialogCancel>
          <Button variant="destructive" :loading="deleteBusy" @click="confirmDelete">
            {{ deleteBusy ? t('floors.delete.busy') : t('floors.delete.confirm') }}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
