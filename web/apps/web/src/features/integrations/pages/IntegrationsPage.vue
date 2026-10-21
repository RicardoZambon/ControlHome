<script setup lang="ts">
import {
  listConnectors,
  listIntegrations,
  type ConnectorDto,
  type IntegrationDto,
} from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Skeleton, cn } from '@controlhome/ui'
import { ChevronRight, FlaskConical, KeyRound, Plus, RefreshCw, Sparkles } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '../../../components/PageHeader.vue'
import IntegrationStatusBadge from '../../../components/integrations/IntegrationStatusBadge.vue'
import { connectorIcon, statusStyle } from '../../../components/integrations/integrationStatus'
import { localized } from '../../../components/schema/schema'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { useRealtimeStore } from '../../../stores/realtime'

/**
 * Admin: the home's integrations with their live status and device count, each opening its own page; "Add
 * integration" opens the catalogue. One that needs a new sign-in says so, with the way to do it.
 */
const { t, locale } = useI18n()
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
const simulated = computed(() => connectorsByKey.value.get('simulated'))

const cards = computed(() =>
  integrations.value.map((integration) => {
    const connector = connectorsByKey.value.get(integration.connectorKey)
    const live = devices.integrationStatusOf(integration)
    return {
      integration,
      connectorName: connector ? localized(connector.displayName, locale.value) : integration.connectorKey,
      icon: connectorIcon(connector?.icon),
      status: live.status,
      attention: statusStyle(live.status).attention ?? false,
      canReauth: live.status === 'NeedsReauth' && (connector?.features.reauth ?? false),
      message: live.message,
    }
  }),
)
</script>

<template>
  <div>
    <PageHeader
      :eyebrow="t('integrations.eyebrow')"
      :title="t('integrations.title')"
      :description="t('integrations.description')"
    >
      <template v-if="integrations.length > 0" #actions>
        <Button as-child class="max-sm:w-full" data-slot="add-integration">
          <RouterLink :to="{ name: 'integration-catalog' }">
            <Plus aria-hidden="true" />
            {{ t('integrations.add') }}
          </RouterLink>
        </Button>
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
        <div class="flex flex-wrap gap-2">
          <Button as-child size="lg">
            <RouterLink :to="{ name: 'integration-catalog' }">
              <Plus aria-hidden="true" />
              {{ t('integrations.empty.browse') }}
            </RouterLink>
          </Button>
          <Button v-if="simulated" as-child size="lg" variant="outline">
            <RouterLink :to="{ name: 'integration-setup', params: { connector: 'simulated' } }">
              <FlaskConical aria-hidden="true" />
              {{ t('integrations.empty.demo') }}
            </RouterLink>
          </Button>
        </div>
      </div>
    </section>

    <ul v-else class="grid gap-4" :aria-label="t('integrations.listLabel')">
      <li
        v-for="card in cards"
        :key="card.integration.id"
        :class="
          cn(
            'group relative flex flex-wrap items-center gap-4 rounded-2xl border bg-card p-5 shadow-xs transition-[border-color,box-shadow] has-[a:focus-visible]:ring-2 has-[a:focus-visible]:ring-ring hover:border-ring/40 hover:shadow-md sm:flex-nowrap',
            card.attention && 'border-state-warn/50',
            !card.integration.enabled && 'bg-muted/40',
          )
        "
        :data-integration-id="card.integration.id"
      >
        <span
          :class="
            cn(
              'inline-flex size-12 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-6',
              !card.integration.enabled && 'opacity-60',
            )
          "
          aria-hidden="true"
        >
          <component :is="card.icon" />
        </span>
        <div class="grid min-w-0 flex-1 gap-1">
          <h2 class="truncate font-serif text-xl font-medium tracking-tight">
            <RouterLink
              :to="{ name: 'integration', params: { id: card.integration.id } }"
              class="outline-none after:absolute after:inset-0 after:content-['']"
            >
              {{ card.integration.name }}
            </RouterLink>
          </h2>
          <!-- Phones: one line each (a "·" left at the end of a wrapped line reads as a typo). -->
          <p class="grid gap-1 text-sm text-muted-foreground sm:flex sm:flex-wrap sm:items-center sm:gap-x-2">
            <template v-if="card.connectorName !== card.integration.name">
              <span>{{ card.connectorName }}</span>
              <span aria-hidden="true" class="max-sm:hidden">·</span>
            </template>
            <span data-slot="device-count">{{
              t(
                'integrations.deviceCount',
                { count: card.integration.deviceCount },
                card.integration.deviceCount,
              )
            }}</span>
            <span aria-hidden="true" class="max-sm:hidden">·</span>
            <span>{{
              t('integrations.added', { date: formatDateTime(card.integration.createdAtUtc, locale) })
            }}</span>
          </p>
          <p v-if="card.message" class="text-sm text-muted-foreground">{{ card.message }}</p>
        </div>
        <div class="flex w-full items-center gap-2 sm:w-auto">
          <IntegrationStatusBadge :status="card.integration.enabled ? card.status : 'Stopped'" />
          <span
            v-if="!card.integration.enabled"
            class="text-xs font-medium text-muted-foreground"
            data-slot="integration-disabled"
          >
            {{ t('integrations.disabled') }}
          </span>
          <Button
            v-if="card.canReauth"
            as-child
            size="sm"
            variant="outline"
            class="relative z-10 ml-auto sm:ml-0"
          >
            <RouterLink :to="{ name: 'integration-reauth', params: { id: card.integration.id } }">
              <KeyRound aria-hidden="true" />
              {{ t('integrations.detail.reauth') }}
            </RouterLink>
          </Button>
          <ChevronRight
            class="ml-auto size-5 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5 sm:ml-0"
            aria-hidden="true"
          />
        </div>
      </li>
    </ul>
  </div>
</template>
