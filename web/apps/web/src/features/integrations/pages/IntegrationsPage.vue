<script setup lang="ts">
import {
  createIntegration,
  deleteIntegration,
  listConnectors,
  listIntegrations,
  type ConnectorDto,
  type IntegrationDto,
} from '@controlhome/api-client'
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
  Badge,
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Skeleton,
  cn,
  toast,
} from '@controlhome/ui'
import { Blocks, ChevronRight, Ellipsis, FlaskConical, Plus, RefreshCw, Sparkles, Trash } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import { localized } from '../../../components/schema/schema'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'

/**
 * Admin: the home's integrations with their live status and device count, and — until the setup flows arrive
 * (milestone 1.3) — one button that adds the simulated demo home (`connector: simulated`, preset `basic`).
 */
const { t, te, locale } = useI18n()
const problem = useProblem()
const devices = useDevicesStore()
const realtime = useRealtimeStore()

const integrations = shallowRef<IntegrationDto[]>([])
const connectors = shallowRef<ConnectorDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)

async function load(quiet = false): Promise<void> {
  if (!quiet) loading.value = true
  loadError.value = null
  try {
    const [list, catalogue] = await Promise.all([
      listIntegrations().then(unwrap),
      connectors.value.length > 0 ? Promise.resolve(connectors.value) : listConnectors().then(unwrap),
    ])
    integrations.value = list
    connectors.value = catalogue
    devices.setIntegrations(list)
  } catch (error) {
    if (!quiet) loadError.value = error
  } finally {
    loading.value = false
  }
}
onMounted(() => void load())

// New devices arrive a moment after an integration starts: refresh the counts when they do.
let reloadTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => realtime.devicesVersion,
  () => {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(() => void load(true), 300)
  },
)
onBeforeUnmount(() => clearTimeout(reloadTimer))

const connectorsByKey = computed(
  () => new Map(connectors.value.map((connector) => [connector.key, connector])),
)
const simulatedAvailable = computed(() => connectors.value.some((connector) => connector.key === 'simulated'))

const CONNECTOR_ICONS: Record<string, Component> = { 'flask-conical': FlaskConical }

interface StatusStyle {
  tone: string
  pulse?: boolean
}

const STATUS: Record<string, StatusStyle> = {
  Ok: { tone: 'bg-state-on' },
  Connecting: { tone: 'bg-state-info', pulse: true },
  Degraded: { tone: 'bg-state-warn' },
  NeedsReauth: { tone: 'bg-state-warn' },
  PlanRequired: { tone: 'bg-state-warn' },
  QuotaExceeded: { tone: 'bg-state-warn' },
  Error: { tone: 'bg-state-danger' },
  Stopped: { tone: 'bg-state-off' },
}

const cards = computed(() =>
  integrations.value.map((integration) => {
    const connector = connectorsByKey.value.get(integration.connectorKey)
    const live = devices.integrationStatusOf(integration)
    return {
      integration,
      connectorName: connector ? localized(connector.displayName, locale.value) : integration.connectorKey,
      icon: (connector && CONNECTOR_ICONS[connector.icon]) ?? Blocks,
      status: live.status,
      statusLabel: te(`integrations.status.${live.status}`)
        ? t(`integrations.status.${live.status}`)
        : live.status,
      statusStyle: STATUS[live.status] ?? { tone: 'bg-state-off' },
      message: live.message,
    }
  }),
)

// --- Add the simulated demo ----------------------------------------------------------------------------------------------
const adding = ref(false)

async function addDemo(): Promise<void> {
  adding.value = true
  try {
    const created = unwrap(
      await createIntegration({
        body: { connectorKey: 'simulated', name: t('integrations.demo.name'), config: { preset: 'basic' } },
      }),
    )
    integrations.value = [...integrations.value, created]
    devices.setIntegrations(integrations.value)
    toast.success(t('integrations.demo.added'))
  } catch (error) {
    problem.toastError(error)
  } finally {
    adding.value = false
  }
}

// --- Delete --------------------------------------------------------------------------------------------------------------
const deleting = ref<IntegrationDto | null>(null)
const deleteOpen = ref(false)
const deleteBusy = ref(false)
const deleteError = ref('')

function askDelete(integration: IntegrationDto): void {
  deleting.value = integration
  deleteError.value = ''
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  const integration = deleting.value
  if (!integration) return
  deleteBusy.value = true
  deleteError.value = ''
  try {
    unwrap(await deleteIntegration({ path: { id: integration.id } }))
    integrations.value = integrations.value.filter((entry) => entry.id !== integration.id)
    devices.setIntegrations(integrations.value)
    deleteOpen.value = false
    toast.success(t('integrations.delete.done', { name: integration.name }))
    void devices.load(true)
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
      :eyebrow="t('integrations.eyebrow')"
      :title="t('integrations.title')"
      :description="t('integrations.description')"
    >
      <template v-if="simulatedAvailable && integrations.length > 0" #actions>
        <!-- One way to add any integration: a picker (setup flows add the real ones in a later version) -->
        <DropdownMenu>
          <DropdownMenuTrigger as-child>
            <Button class="max-sm:w-full" :loading="adding" data-slot="add-integration">
              <Plus aria-hidden="true" />
              {{ t('integrations.add') }}
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" class="w-72">
            <DropdownMenuItem @select="addDemo">
              <FlaskConical aria-hidden="true" />
              {{ t('integrations.demo.option') }}
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <p class="flex items-start gap-2 px-2 py-1.5 text-xs text-muted-foreground">
              <Sparkles class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {{ t('integrations.comingSoon') }}
            </p>
          </DropdownMenuContent>
        </DropdownMenu>
      </template>
    </PageHeader>

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('integrations.loadFailed') }} {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load()">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <ul v-else-if="loading" class="grid gap-4" :aria-label="t('integrations.listLabel')">
      <li v-for="index in 2" :key="index" class="flex items-center gap-4 rounded-2xl border bg-card p-5">
        <Skeleton class="size-12 rounded-xl" />
        <div class="grid flex-1 gap-2">
          <Skeleton class="h-5 w-48" />
          <Skeleton class="h-3 w-32" />
        </div>
      </li>
    </ul>

    <!-- None yet -->
    <section
      v-else-if="integrations.length === 0"
      class="relative grid gap-6 overflow-hidden rounded-2xl border bg-card p-6 shadow-sm sm:p-10"
      aria-labelledby="integrations-empty"
    >
      <div
        class="pointer-events-none absolute inset-0 bg-radial-[at_100%_0%] from-accent via-transparent to-transparent"
        aria-hidden="true"
      />
      <div class="relative grid max-w-xl gap-4">
        <span
          class="inline-flex size-12 items-center justify-center rounded-2xl bg-accent text-accent-foreground"
          aria-hidden="true"
        >
          <Sparkles class="size-6" />
        </span>
        <h2 id="integrations-empty" class="font-serif text-3xl font-medium tracking-tight">
          {{ t('integrations.empty.title') }}
        </h2>
        <p class="text-muted-foreground">{{ t('integrations.empty.description') }}</p>
        <div v-if="simulatedAvailable">
          <Button size="lg" :loading="adding" @click="addDemo">
            <FlaskConical aria-hidden="true" />
            {{ t('integrations.demo.add') }}
          </Button>
        </div>
      </div>
    </section>

    <template v-else>
      <ul class="grid gap-4" :aria-label="t('integrations.listLabel')">
        <li
          v-for="card in cards"
          :key="card.integration.id"
          class="flex flex-wrap items-center gap-4 rounded-2xl border bg-card p-5 shadow-xs sm:flex-nowrap"
          :data-integration-id="card.integration.id"
        >
          <span
            class="inline-flex size-12 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-6"
            aria-hidden="true"
          >
            <component :is="card.icon" />
          </span>
          <div class="grid min-w-0 flex-1 gap-1">
            <h2 class="truncate font-serif text-xl font-medium tracking-tight">
              {{ card.integration.name }}
            </h2>
            <!-- Phones: one line each (a "·" left at the end of a wrapped line reads as a typo). -->
            <p
              class="grid gap-1 text-sm text-muted-foreground sm:flex sm:flex-wrap sm:items-center sm:gap-x-2"
            >
              <!-- The connector only when its name says something the integration's own name does not. -->
              <template v-if="card.connectorName !== card.integration.name">
                <span>{{ card.connectorName }}</span>
                <span aria-hidden="true" class="max-sm:hidden">·</span>
              </template>
              <span>{{
                t('integrations.added', { date: formatDateTime(card.integration.createdAtUtc, locale) })
              }}</span>
            </p>
            <p v-if="card.message" class="text-sm text-muted-foreground">{{ card.message }}</p>
          </div>
          <div class="flex w-full items-center gap-2 sm:w-auto">
            <Badge variant="outline" class="gap-1.5 bg-card py-1" data-slot="integration-status">
              <span class="relative flex size-2" aria-hidden="true">
                <span
                  v-if="card.statusStyle.pulse"
                  :class="
                    cn(
                      'absolute inset-0 rounded-full opacity-60 motion-safe:animate-ping',
                      card.statusStyle.tone,
                    )
                  "
                />
                <span :class="cn('relative size-2 rounded-full', card.statusStyle.tone)" />
              </span>
              {{ card.statusLabel }}
            </Badge>
            <Button variant="ghost" size="sm" as-child class="ml-auto sm:ml-0">
              <RouterLink :to="{ name: 'devices', query: { integration: card.integration.id } }">
                {{
                  t(
                    'integrations.deviceCount',
                    { count: card.integration.deviceCount },
                    card.integration.deviceCount,
                  )
                }}
                <ChevronRight aria-hidden="true" />
              </RouterLink>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger as-child>
                <Button
                  variant="ghost"
                  size="icon"
                  class="size-11"
                  :aria-label="t('integrations.actions', { name: card.integration.name })"
                >
                  <Ellipsis aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem variant="destructive" @select="askDelete(card.integration)">
                  <Trash aria-hidden="true" />
                  {{ t('common.delete') }}
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </li>
      </ul>
    </template>

    <AlertDialog :open="deleteOpen" @update:open="onDeleteOpenChange">
      <AlertDialogContent v-if="deleting">
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('integrations.delete.title', { name: deleting.name }) }}
          </AlertDialogTitle>
          <AlertDialogDescription>
            {{ t('integrations.delete.description', { count: deleting.deviceCount }, deleting.deviceCount) }}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <FormAlert v-if="deleteError" :message="deleteError" />
        <AlertDialogFooter>
          <AlertDialogCancel :disabled="deleteBusy">{{ t('common.cancel') }}</AlertDialogCancel>
          <Button variant="destructive" :loading="deleteBusy" @click="confirmDelete">
            {{ t('integrations.delete.confirm') }}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
