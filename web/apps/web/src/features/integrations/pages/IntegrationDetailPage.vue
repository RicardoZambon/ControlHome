<script setup lang="ts">
import {
  deleteIntegration,
  listConnectors,
  listIntegrations,
  updateIntegration,
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
  Button,
  Label,
  Skeleton,
  Switch,
  cn,
  toast,
} from '@controlhome/ui'
import {
  ArrowLeft,
  ChevronRight,
  EyeOff,
  KeyRound,
  RefreshCw,
  SearchX,
  SlidersHorizontal,
  Trash,
  TriangleAlert,
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import FormAlert from '../../../components/FormAlert.vue'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import StateBadge from '../../../components/devices/StateBadge.vue'
import { deviceKindLabel } from '../../../components/devices/format'
import IntegrationStatusBadge from '../../../components/integrations/IntegrationStatusBadge.vue'
import { connectorIcon, statusStyle } from '../../../components/integrations/integrationStatus'
import { localized } from '../../../components/schema/schema'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'

/**
 * Admin: one integration — its live status (and a way to sign in again when it needs one), its devices (orphaned ones
 * flagged), turning it off and on, and removing it with everything it brought.
 */
const { t, te, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const problem = useProblem()
const devices = useDevicesStore()
const realtime = useRealtimeStore()
const enabledId = useId()

const integrationId = computed(() => String(route.params.id))
const integration = shallowRef<IntegrationDto | null>(null)
const connector = shallowRef<ConnectorDto | undefined>()
const loading = ref(true)
const loadError = ref<unknown>(null)
const notFound = ref(false)

async function load(quiet = false): Promise<void> {
  if (!quiet) loading.value = true
  loadError.value = null
  try {
    const [list, connectors] = await Promise.all([
      listIntegrations().then(unwrap),
      connector.value ? Promise.resolve(null) : listConnectors().then(unwrap),
      devices.load(),
    ])
    devices.setIntegrations(list)
    integration.value = list.find((entry) => entry.id === integrationId.value) ?? null
    notFound.value = integration.value === null
    if (connectors && integration.value)
      connector.value = connectors.find((entry) => entry.key === integration.value!.connectorKey)
  } catch (error) {
    if (!quiet) loadError.value = error
  } finally {
    loading.value = false
  }
}
watch(integrationId, () => void load(), { immediate: true })
onMounted(() => void devices.load())

let reloadTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => realtime.devicesVersion,
  () => {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(() => void load(true), 300)
  },
)
onBeforeUnmount(() => clearTimeout(reloadTimer))

usePageTrail(() =>
  integration.value
    ? [{ label: t('integrations.title'), to: { name: 'integrations' } }, { label: integration.value.name }]
    : null,
)

const live = computed(() => (integration.value ? devices.integrationStatusOf(integration.value) : null))
const status = computed(() =>
  integration.value && !integration.value.enabled ? 'Stopped' : (live.value?.status ?? 'Connecting'),
)
const attention = computed(() => statusStyle(status.value).attention ?? false)
const canReauth = computed(() => connector.value?.features.reauth ?? false)
/** It signs in with credentials (a token, a password): "Sign in again"; otherwise the same flow changes its settings. */
const usesCredentials = computed(() => connector.value?.configFields.some((field) => field.secret) ?? false)
const connectorName = computed(() =>
  connector.value ? localized(connector.value.displayName, locale.value) : integration.value?.connectorKey,
)
const icon = computed(() => connectorIcon(connector.value?.icon))

const deviceRows = computed(() =>
  devices.devices
    .filter((device) => device.integrationId === integrationId.value)
    .sort((a, b) => Number(a.isOrphaned) - Number(b.isOrphaned) || a.name.localeCompare(b.name))
    .map((device) => ({
      device,
      kind: deviceKindLabel(device, t, te),
      availability: devices.availabilityOf(device),
      capabilities: device.endpoints.flatMap((endpoint) => endpoint.capabilities.map((c) => c.capabilityId)),
      /** Left out of dashboards and lists (every endpoint hidden, e.g. in the import review). */
      hidden: device.endpoints.length > 0 && device.endpoints.every((endpoint) => endpoint.hidden),
    })),
)
const orphanedCount = computed(() => deviceRows.value.filter((row) => row.device.isOrphaned).length)

/** The devices by room, in the home's room order; devices in no room last. */
const deviceGroups = computed(() => {
  const order = new Map(devices.rooms.map((room, index) => [room.id, index]))
  const groups = new Map<number | null, typeof deviceRows.value>()
  for (const row of deviceRows.value) {
    const roomId = row.device.roomId !== null && order.has(row.device.roomId) ? row.device.roomId : null
    groups.set(roomId, [...(groups.get(roomId) ?? []), row])
  }
  return [...groups.entries()]
    .sort(([a], [b]) => (a === null ? 1 : b === null ? -1 : order.get(a)! - order.get(b)!))
    .map(([roomId, rows]) => ({
      key: roomId ?? 'none',
      name: roomId === null ? t('integrations.detail.noRoom') : devices.roomsById.get(roomId)!.name,
      rows,
    }))
})

// --- Enable / disable --------------------------------------------------------------------------------------------------
const toggling = ref(false)

async function setEnabled(enabled: boolean): Promise<void> {
  const current = integration.value
  if (!current) return
  toggling.value = true
  try {
    integration.value = unwrap(
      await updateIntegration({ path: { id: current.id }, body: { name: current.name, enabled } }),
    )
    toast.success(
      enabled
        ? t('integrations.detail.enabledToast', { name: current.name })
        : t('integrations.detail.disabledToast', { name: current.name }),
    )
  } catch (error) {
    problem.toastError(error)
  } finally {
    toggling.value = false
  }
}

// --- Remove --------------------------------------------------------------------------------------------------------------
const deleteOpen = ref(false)
const deleteBusy = ref(false)
const deleteError = ref('')

async function confirmDelete(): Promise<void> {
  const current = integration.value
  if (!current) return
  deleteBusy.value = true
  deleteError.value = ''
  try {
    unwrap(await deleteIntegration({ path: { id: current.id } }))
    deleteOpen.value = false
    toast.success(t('integrations.delete.done', { name: current.name }))
    void devices.load(true)
    await router.push({ name: 'integrations' })
  } catch (error) {
    deleteError.value = problem.message(error)
  } finally {
    deleteBusy.value = false
  }
}

function onDeleteOpenChange(open: boolean): void {
  deleteOpen.value = open || deleteBusy.value
  if (open) deleteError.value = ''
}
</script>

<template>
  <div class="mx-auto w-full max-w-4xl">
    <RouterLink
      :to="{ name: 'integrations' }"
      class="mb-4 inline-flex items-center gap-1.5 rounded-md text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
    >
      <ArrowLeft class="size-4" aria-hidden="true" />
      {{ t('integrations.title') }}
    </RouterLink>

    <div v-if="loading && !integration" class="grid gap-6" role="status" :aria-label="t('common.loading')">
      <Skeleton class="h-16 w-96 max-w-full" />
      <Skeleton class="h-40 rounded-2xl" />
    </div>

    <Alert v-else-if="loadError" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load()">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <section
      v-else-if="notFound"
      class="grid justify-items-center gap-4 rounded-2xl border border-dashed px-6 py-16 text-center"
    >
      <SearchX class="size-10 text-muted-foreground" aria-hidden="true" />
      <h1 class="font-serif text-3xl font-medium tracking-tight">{{ t('integrations.detail.notFound') }}</h1>
      <p class="max-w-md text-muted-foreground">{{ t('errors.integrations.not_found') }}</p>
    </section>

    <template v-else-if="integration">
      <header class="mb-8 flex flex-wrap items-start gap-4 sm:items-center sm:gap-5">
        <span
          :class="
            cn(
              'inline-flex size-16 shrink-0 items-center justify-center rounded-2xl bg-accent text-accent-foreground shadow-xs [&_svg]:size-8',
              !integration.enabled && 'opacity-60',
            )
          "
          aria-hidden="true"
        >
          <component :is="icon" />
        </span>
        <div class="grid min-w-0 flex-1 gap-1.5">
          <p class="eyebrow truncate">{{ connectorName }}</p>
          <h1 class="font-serif text-3xl leading-tight font-medium tracking-tight break-words md:text-4xl">
            {{ integration.name }}
          </h1>
          <div class="flex flex-wrap items-center gap-2">
            <IntegrationStatusBadge :status="status" />
            <span class="text-sm text-muted-foreground">
              {{ t('integrations.added', { date: formatDateTime(integration.createdAtUtc, locale) }) }}
            </span>
          </div>
        </div>
      </header>

      <!-- Needs someone: sign in again, or what went wrong -->
      <div
        v-if="integration.enabled && (attention || live?.message)"
        :class="
          cn(
            'mb-8 flex flex-wrap items-start gap-4 rounded-2xl border p-5',
            attention ? 'border-state-warn/50 bg-state-warn/10' : 'bg-card',
          )
        "
        role="status"
        data-slot="integration-attention"
      >
        <TriangleAlert v-if="attention" class="mt-0.5 size-5 shrink-0 text-state-warn" aria-hidden="true" />
        <div class="grid min-w-0 flex-1 gap-1">
          <p class="font-medium">
            {{
              status === 'NeedsReauth'
                ? t('integrations.detail.needsReauthTitle')
                : t('integrations.detail.statusTitle')
            }}
          </p>
          <p class="text-sm text-muted-foreground">
            {{ live?.message ?? t('integrations.detail.needsReauth') }}
          </p>
        </div>
        <Button v-if="canReauth && status === 'NeedsReauth'" as-child>
          <RouterLink :to="{ name: 'integration-reauth', params: { id: integration.id } }">
            <KeyRound aria-hidden="true" />
            {{ t('integrations.detail.reauth') }}
          </RouterLink>
        </Button>
      </div>

      <div class="grid gap-8 lg:grid-cols-[minmax(0,1fr)_17rem] lg:items-start">
        <!-- Devices -->
        <section class="grid gap-3" aria-labelledby="integration-devices">
          <div class="flex items-baseline justify-between gap-3">
            <h2 id="integration-devices" class="eyebrow tabular-nums">
              {{ t('integrations.detail.devices', { count: deviceRows.length }) }}
            </h2>
            <RouterLink
              v-if="deviceRows.length > 0"
              :to="{ name: 'devices', query: { integration: integration.id } }"
              class="text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              {{ t('integrations.detail.openGrid') }}
            </RouterLink>
          </div>
          <p v-if="orphanedCount > 0" class="text-sm text-muted-foreground">
            {{ t('integrations.detail.orphaned', orphanedCount) }}
          </p>
          <p
            v-if="deviceRows.length === 0"
            class="rounded-2xl border border-dashed p-6 text-sm text-muted-foreground"
          >
            {{
              integration.enabled ? t('integrations.detail.noDevices') : t('integrations.detail.noDevicesOff')
            }}
          </p>
          <div
            v-else
            class="grid divide-y overflow-hidden rounded-2xl border bg-card shadow-xs"
            data-slot="integration-devices"
          >
            <section v-for="group in deviceGroups" :key="group.key" :aria-label="group.name">
              <h3 class="eyebrow bg-muted/40 px-4 py-2">{{ group.name }}</h3>
              <ul class="grid divide-y">
                <li v-for="row in group.rows" :key="row.device.id">
                  <RouterLink
                    :to="{ name: 'device', params: { id: row.device.id } }"
                    class="group flex items-center gap-3 px-4 py-3 outline-none hover:bg-accent/50 focus-visible:bg-accent/50 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
                  >
                    <span class="relative shrink-0">
                      <DeviceIcon
                        size="sm"
                        :icon="row.device.icon"
                        :device-class="row.device.deviceClass"
                        :capabilities="row.capabilities"
                        :class="row.device.isOrphaned && 'opacity-60'"
                      />
                      <!-- Reachable or not, at a glance -->
                      <span
                        :class="
                          cn(
                            'absolute -right-0.5 -bottom-0.5 size-2.5 rounded-full border-2 border-card',
                            row.availability === 'Online'
                              ? 'bg-state-on'
                              : row.availability === 'Offline'
                                ? 'bg-state-offline'
                                : 'bg-muted-foreground/50',
                          )
                        "
                        aria-hidden="true"
                        data-slot="device-availability"
                        :data-availability="row.availability"
                      />
                    </span>
                    <span class="grid min-w-0 flex-1">
                      <span class="flex min-w-0 items-center gap-1.5">
                        <span class="truncate text-sm font-medium">{{ row.device.name }}</span>
                        <EyeOff
                          v-if="row.hidden"
                          class="size-3.5 shrink-0 text-muted-foreground"
                          :aria-label="t('device.hidden')"
                          role="img"
                          data-slot="device-hidden"
                        />
                      </span>
                      <span class="truncate text-xs text-muted-foreground">
                        <!-- Phones have no room for the badge: the line under the name says it -->
                        <span v-if="row.device.isOrphaned" class="font-medium text-foreground sm:hidden">
                          {{ t('device.orphaned') }} ·
                        </span>
                        {{ row.kind }}
                      </span>
                    </span>
                    <StateBadge
                      v-if="row.device.isOrphaned || row.availability !== 'Online'"
                      :availability="row.availability"
                      :orphaned="row.device.isOrphaned"
                      class="max-sm:hidden"
                    />
                    <ChevronRight
                      class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                      aria-hidden="true"
                    />
                  </RouterLink>
                </li>
              </ul>
            </section>
          </div>
        </section>

        <!-- Settings: on phones before the (long) device list -->
        <aside class="grid gap-4 max-lg:order-first" :aria-label="t('integrations.detail.settings')">
          <div class="grid gap-3 rounded-2xl border bg-card p-4 shadow-xs">
            <div class="flex items-center justify-between gap-3">
              <Label :for="enabledId" class="text-sm font-medium">{{
                t('integrations.detail.enabled')
              }}</Label>
              <Switch
                :id="enabledId"
                :model-value="integration.enabled"
                :disabled="toggling"
                data-slot="integration-enabled"
                @update:model-value="(value: boolean) => setEnabled(value)"
              />
            </div>
            <p class="text-xs text-muted-foreground">{{ t('integrations.detail.enabledHelp') }}</p>
          </div>

          <Button v-if="canReauth" as-child variant="outline" class="justify-start">
            <RouterLink :to="{ name: 'integration-reauth', params: { id: integration.id } }">
              <KeyRound v-if="usesCredentials" aria-hidden="true" />
              <SlidersHorizontal v-else aria-hidden="true" />
              {{ usesCredentials ? t('integrations.detail.reauth') : t('integrations.detail.reconfigure') }}
            </RouterLink>
          </Button>

          <Button
            variant="outline"
            class="justify-start text-destructive hover:text-destructive"
            data-slot="integration-remove"
            @click="deleteOpen = true"
          >
            <Trash aria-hidden="true" />
            {{ t('integrations.detail.remove') }}
          </Button>
        </aside>
      </div>

      <AlertDialog :open="deleteOpen" @update:open="onDeleteOpenChange">
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
              {{ t('integrations.delete.title', { name: integration.name }) }}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {{
                t(
                  'integrations.delete.description',
                  { count: integration.deviceCount },
                  integration.deviceCount,
                )
              }}
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
    </template>
  </div>
</template>
