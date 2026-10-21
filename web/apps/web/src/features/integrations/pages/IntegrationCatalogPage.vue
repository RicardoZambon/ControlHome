<script setup lang="ts">
import { listConnectors, type ConnectorDto } from '@controlhome/api-client'
import { Alert, AlertDescription, Badge, Button, Input, Skeleton } from '@controlhome/ui'
import { ArrowLeft, ChevronRight, RefreshCw, Search } from '@lucide/vue'
import { computed, onMounted, ref, shallowRef, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '../../../components/PageHeader.vue'
import { normalizeText } from '../../../components/floorplan/placements'
import { connectorIcon } from '../../../components/integrations/integrationStatus'
import { localized } from '../../../components/schema/schema'
import { usePageTrail } from '../../../composables/usePageTrail'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'

/**
 * Admin: every connector this ControlHome has (`GET /api/connectors`), by kind — vendor clouds and hubs, protocols on
 * the home network, virtual ones — each with what it can do. Choosing one starts its guided setup.
 */
const { t, te, locale } = useI18n()
const problem = useProblem()
const searchId = useId()

const connectors = shallowRef<ConnectorDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)
const query = ref('')

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    connectors.value = unwrap(await listConnectors())
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
onMounted(() => void load())

usePageTrail(() => [
  { label: t('integrations.title'), to: { name: 'integrations' } },
  { label: t('integrations.catalog.title') },
])

const CATEGORIES = ['vendor', 'protocol', 'virtual'] as const

/** Feature chips: how it connects and what the setup does. */
function featuresOf(connector: ConnectorDto): string[] {
  const chips = [
    connector.features.cloud ? t('integrations.features.cloud') : t('integrations.features.local'),
  ]
  if (connector.features.setupFlow) chips.push(t('integrations.features.guided'))
  if (connector.features.pushInventory) chips.push(t('integrations.features.discovery'))
  if (connector.features.permitJoin) chips.push(t('integrations.features.pairing'))
  return chips
}

const groups = computed(() => {
  const needle = normalizeText(query.value)
  const matching = connectors.value.filter((connector) => {
    if (!needle) return true
    const name = normalizeText(localized(connector.displayName, locale.value))
    return name.includes(needle) || connector.key.includes(needle)
  })
  const known = new Set<string>(CATEGORIES)
  return [...CATEGORIES, 'other']
    .map((category) => ({
      category,
      label: te(`integrations.catalog.categories.${category}`)
        ? t(`integrations.catalog.categories.${category}`)
        : category,
      connectors: matching
        .filter((connector) =>
          category === 'other' ? !known.has(connector.category) : connector.category === category,
        )
        .map((connector) => ({
          connector,
          name: localized(connector.displayName, locale.value),
          icon: connectorIcon(connector.icon),
          description: te(`integrations.catalog.about.${connector.key}`)
            ? t(`integrations.catalog.about.${connector.key}`)
            : '',
          features: featuresOf(connector),
        })),
    }))
    .filter((group) => group.connectors.length > 0)
})
</script>

<template>
  <div>
    <RouterLink
      :to="{ name: 'integrations' }"
      class="mb-4 inline-flex items-center gap-1.5 rounded-md text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
    >
      <ArrowLeft class="size-4" aria-hidden="true" />
      {{ t('integrations.title') }}
    </RouterLink>

    <PageHeader
      :eyebrow="t('integrations.eyebrow')"
      :title="t('integrations.catalog.title')"
      :description="t('integrations.catalog.description')"
    >
      <template #actions>
        <div class="relative w-full sm:w-64">
          <label :for="searchId" class="sr-only">{{ t('integrations.catalog.search') }}</label>
          <Search
            class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            :id="searchId"
            v-model="query"
            type="search"
            class="pl-9"
            :placeholder="t('integrations.catalog.search')"
          />
        </div>
      </template>
    </PageHeader>

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <div v-else-if="loading" class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
      <Skeleton v-for="index in 3" :key="index" class="h-44 rounded-2xl" />
    </div>

    <p v-else-if="groups.length === 0" class="text-muted-foreground">{{ t('common.noResults') }}</p>

    <div v-else class="grid gap-10">
      <section
        v-for="group in groups"
        :key="group.category"
        class="grid gap-4"
        :aria-labelledby="`catalog-${group.category}`"
      >
        <h2 :id="`catalog-${group.category}`" class="eyebrow">{{ group.label }}</h2>
        <ul class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          <li
            v-for="entry in group.connectors"
            :key="entry.connector.key"
            class="group relative flex flex-col gap-4 rounded-2xl border bg-card p-5 shadow-xs transition-[border-color,box-shadow] has-[a:focus-visible]:ring-2 has-[a:focus-visible]:ring-ring hover:border-ring/40 hover:shadow-md"
            :data-connector="entry.connector.key"
          >
            <div class="flex items-start justify-between gap-3">
              <span
                class="inline-flex size-12 items-center justify-center rounded-xl bg-accent text-accent-foreground [&_svg]:size-6"
                aria-hidden="true"
              >
                <component :is="entry.icon" />
              </span>
              <ChevronRight
                class="size-5 text-muted-foreground transition-transform group-hover:translate-x-0.5"
                aria-hidden="true"
              />
            </div>
            <div class="grid gap-1">
              <h3 class="font-serif text-xl font-medium tracking-tight">
                <RouterLink
                  :to="{ name: 'integration-setup', params: { connector: entry.connector.key } }"
                  class="outline-none after:absolute after:inset-0 after:content-['']"
                >
                  {{ entry.name }}
                </RouterLink>
              </h3>
              <p v-if="entry.description" class="text-sm text-muted-foreground">{{ entry.description }}</p>
            </div>
            <div class="mt-auto flex flex-wrap gap-1.5">
              <Badge v-for="feature in entry.features" :key="feature" variant="secondary" class="font-normal">
                {{ feature }}
              </Badge>
            </div>
          </li>
        </ul>
      </section>
    </div>
  </div>
</template>
