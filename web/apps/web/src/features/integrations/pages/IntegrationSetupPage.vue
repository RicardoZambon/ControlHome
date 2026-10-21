<script setup lang="ts">
import {
  listConnectors,
  listIntegrations,
  startIntegrationFlow,
  startIntegrationReauthFlow,
  type ConnectorDto,
  type IntegrationDto,
} from '@controlhome/api-client'
import { Alert, AlertDescription, Button, Card, CardContent, Skeleton, cn, toast } from '@controlhome/ui'
import { ArrowLeft, Check, RefreshCw } from '@lucide/vue'
import { computed, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { connectorIcon } from '../../../components/integrations/integrationStatus'
import { localized } from '../../../components/schema/schema'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import FlowWizard from '../components/FlowWizard.vue'
import ImportReview from '../components/ImportReview.vue'

/**
 * Admin: sets up a new integration (`/integrations/add/:connector`) — the connector's flow, then the import review of
 * the devices it brought — or signs an existing one in again (`/integrations/:id/reauth`), keeping its devices.
 */
const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const problem = useProblem()
const devices = useDevicesStore()

const reauthId = computed(() => (route.name === 'integration-reauth' ? String(route.params.id) : null))
const connectorKey = computed(() => (reauthId.value ? null : String(route.params.connector)))

const connector = shallowRef<ConnectorDto | undefined>()
const integration = shallowRef<IntegrationDto | undefined>()
const loading = ref(true)
const loadError = ref<unknown>(null)
const notFound = ref(false)
const phase = ref<'flow' | 'review'>('flow')
const createdId = ref<string | null>(null)
/** Bumped to start the flow again from scratch. */
const attempt = ref(0)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  notFound.value = false
  phase.value = 'flow'
  try {
    const [connectors, integrations] = await Promise.all([
      listConnectors().then(unwrap),
      reauthId.value ? listIntegrations().then(unwrap) : Promise.resolve([]),
    ])
    integration.value = reauthId.value ? integrations.find((entry) => entry.id === reauthId.value) : undefined
    const key = integration.value?.connectorKey ?? connectorKey.value
    connector.value = connectors.find((entry) => entry.key === key)
    notFound.value = !connector.value || (reauthId.value !== null && !integration.value)
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
watch([reauthId, connectorKey], load, { immediate: true })

const name = computed(() => (connector.value ? localized(connector.value.displayName, locale.value) : ''))
const icon = computed(() => connectorIcon(connector.value?.icon))

usePageTrail(() => {
  if (!connector.value) return null
  if (integration.value)
    return [
      { label: t('integrations.title'), to: { name: 'integrations' } },
      { label: integration.value.name, to: { name: 'integration', params: { id: integration.value.id } } },
      { label: t('integrations.setup.reauthTitle') },
    ]
  return [
    { label: t('integrations.title'), to: { name: 'integrations' } },
    { label: t('integrations.catalog.title'), to: { name: 'integration-catalog' } },
    { label: name.value },
  ]
})

function start() {
  return reauthId.value
    ? startIntegrationReauthFlow({ path: { id: reauthId.value } }).then(unwrap)
    : startIntegrationFlow({ body: { connectorKey: connectorKey.value ?? '' } }).then(unwrap)
}

function onDone(integrationId: string): void {
  void devices.load(true)
  if (reauthId.value) {
    toast.success(t('integrations.setup.reauthDone', { name: integration.value?.name ?? name.value }))
    void router.push({ name: 'integration', params: { id: integrationId } })
    return
  }
  createdId.value = integrationId
  // A moment for the "connected" check mark before the review.
  setTimeout(() => (phase.value = 'review'), 700)
}

function onCancel(): void {
  if (reauthId.value) void router.push({ name: 'integration', params: { id: reauthId.value } })
  else void router.push({ name: 'integration-catalog' })
}

function finish(): void {
  if (createdId.value) void router.push({ name: 'integration', params: { id: createdId.value } })
}

const steps = computed(() =>
  reauthId.value
    ? []
    : [
        { id: 'flow', label: t('integrations.setup.stepConnect') },
        { id: 'review', label: t('integrations.setup.stepReview') },
      ],
)
</script>

<template>
  <div class="mx-auto w-full max-w-3xl">
    <RouterLink
      :to="reauthId ? { name: 'integration', params: { id: reauthId } } : { name: 'integration-catalog' }"
      class="mb-4 inline-flex items-center gap-1.5 rounded-md text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
    >
      <ArrowLeft class="size-4" aria-hidden="true" />
      {{ reauthId ? (integration?.name ?? t('integrations.title')) : t('integrations.catalog.title') }}
    </RouterLink>

    <div v-if="loading" class="grid gap-6">
      <Skeleton class="h-16 w-80 max-w-full" />
      <Skeleton class="h-80 rounded-2xl" />
    </div>

    <Alert v-else-if="loadError" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <section
      v-else-if="notFound"
      class="grid justify-items-center gap-4 rounded-2xl border border-dashed px-6 py-16 text-center"
    >
      <h1 class="font-serif text-3xl font-medium tracking-tight">{{ t('integrations.setup.notFound') }}</h1>
      <Button as-child variant="outline">
        <RouterLink :to="{ name: 'integration-catalog' }">{{ t('integrations.catalog.title') }}</RouterLink>
      </Button>
    </section>

    <template v-else>
      <header class="mb-8 flex items-center gap-4">
        <span
          class="inline-flex size-14 shrink-0 items-center justify-center rounded-2xl bg-accent text-accent-foreground shadow-xs [&_svg]:size-7"
          aria-hidden="true"
        >
          <component :is="icon" />
        </span>
        <div class="grid min-w-0 gap-1">
          <p class="eyebrow">
            {{ reauthId ? t('integrations.setup.reauthEyebrow') : t('integrations.setup.eyebrow') }}
          </p>
          <h1 class="truncate font-serif text-3xl font-medium tracking-tight md:text-4xl">
            {{ reauthId ? (integration?.name ?? name) : name }}
          </h1>
        </div>
      </header>

      <!-- Connect → Review devices -->
      <ol
        v-if="steps.length > 0"
        class="mb-6 flex items-center gap-3"
        :aria-label="t('integrations.setup.progress')"
      >
        <li
          v-for="(entry, index) in steps"
          :key="entry.id"
          class="flex min-w-0 items-center gap-3"
          :class="index < steps.length - 1 ? 'flex-auto' : ''"
          :aria-current="phase === entry.id ? 'step' : undefined"
        >
          <span
            :class="
              cn(
                'flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold tabular-nums transition-colors',
                phase === entry.id && 'border-primary bg-primary text-primary-foreground',
                phase === 'review' &&
                  entry.id === 'flow' &&
                  'border-state-on bg-state-on text-state-on-foreground',
              )
            "
            aria-hidden="true"
          >
            <Check v-if="phase === 'review' && entry.id === 'flow'" class="size-4" />
            <template v-else>{{ index + 1 }}</template>
          </span>
          <span :class="cn('truncate text-sm', phase === entry.id ? 'font-medium' : 'text-muted-foreground')">
            {{ entry.label }}
          </span>
          <span v-if="index < steps.length - 1" class="h-px flex-1 bg-border" aria-hidden="true" />
        </li>
      </ol>

      <Card class="gap-0 py-0 shadow-md">
        <CardContent class="p-6 sm:p-8">
          <FlowWizard
            v-if="phase === 'flow'"
            :key="attempt"
            :start="start"
            :connector="connector"
            @done="onDone"
            @cancel="onCancel"
          />
          <ImportReview v-else-if="createdId" :integration-id="createdId" @finish="finish" />
        </CardContent>
      </Card>
    </template>
  </div>
</template>
