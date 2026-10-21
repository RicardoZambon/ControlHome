<script setup lang="ts">
import { listEnergyHourly, type EndpointDto, type EnergyHourlyDto } from '@controlhome/api-client'
import { Button, Skeleton } from '@controlhome/ui'
import { RefreshCw, Zap } from '@lucide/vue'
import { computed, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import EnergyBars from '../../../components/charts/EnergyBars.vue'
import {
  energyBucket,
  rangeQuery,
  timeWindow,
  totalWh,
  type HistoryRange,
} from '../../../components/charts/chartOptions'
import { formatMeasurement } from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'

/**
 * The energy a device used over the chosen range (`GET /api/energy/hourly` for each of its endpoints that has an energy
 * meter or power readings): the total, and bars per hour (a day) or per day (a week, a month).
 */
const props = defineProps<{
  endpoints: readonly EndpointDto[]
  range: HistoryRange
  refresh: number
}>()

const { t, locale } = useI18n()
const problem = useProblem()

const rows = shallowRef<EnergyHourlyDto[] | null>(null)
const loading = ref(true)
const error = ref<unknown>(null)
const fetchedAt = ref(Date.now())

async function load(): Promise<void> {
  error.value = null
  const now = Date.now()
  try {
    const lists = await Promise.all(
      props.endpoints.map(async (endpoint) =>
        unwrap(
          await listEnergyHourly({ query: { ...rangeQuery(props.range, now), endpointId: endpoint.id } }),
        ),
      ),
    )
    rows.value = lists.flat()
    fetchedAt.value = now
  } catch (failure) {
    error.value = failure
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.range, props.endpoints.map((endpoint) => endpoint.id).join()] as const,
  () => {
    loading.value = true
    void load()
  },
  { immediate: true },
)
watch(
  () => props.refresh,
  () => void load(),
)

const total = computed(() => formatMeasurement(totalWh(rows.value ?? []), 'Wh', locale.value))
const estimated = computed(() => (rows.value ?? []).some((row) => row.estimated))
const newest = computed(() => {
  const times = (rows.value ?? []).map((row) => Date.parse(row.hourStartUtc) + 3_600_000)
  return times.length > 0 ? Math.max(...times) : undefined
})
const chartWindow = computed(() => timeWindow(props.range, fetchedAt.value, newest.value))
const label = computed(() => t('history.energyLabel', { range: t(`history.ranges.${props.range}`) }))
</script>

<template>
  <section
    class="grid gap-4 rounded-2xl border bg-card p-5 shadow-xs"
    :aria-label="t('history.energy')"
    data-slot="energy-history"
  >
    <header class="flex items-start justify-between gap-3">
      <div class="flex min-w-0 items-center gap-3">
        <span
          class="flex size-9 shrink-0 items-center justify-center rounded-lg bg-state-energy/15 text-state-energy"
          aria-hidden="true"
        >
          <Zap class="size-4" />
        </span>
        <div class="grid min-w-0 gap-0.5">
          <h3 class="truncate font-medium">{{ t('history.energy') }}</h3>
          <p class="truncate text-xs text-muted-foreground">
            {{ energyBucket(range) === 'hour' ? t('history.perHour') : t('history.perDay') }}
          </p>
        </div>
      </div>
      <div class="grid shrink-0 justify-items-end gap-0.5 text-right">
        <span
          class="font-serif text-2xl leading-none font-medium tracking-tight tabular-nums"
          data-slot="energy-total"
        >
          {{ rows ? total : '—' }}
        </span>
        <span v-if="estimated" class="text-xs text-muted-foreground">{{ t('history.partlyEstimated') }}</span>
      </div>
    </header>

    <Skeleton v-if="loading && !rows" class="h-52 w-full rounded-xl" />
    <div
      v-else-if="error && !rows"
      class="grid h-52 place-items-center rounded-xl border border-dashed p-4 text-center text-sm text-muted-foreground"
    >
      <div class="grid justify-items-center gap-2">
        <p>{{ problem.message(error) }}</p>
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw aria-hidden="true" />
          {{ t('common.retry') }}
        </Button>
      </div>
    </div>
    <EnergyBars
      v-else-if="rows"
      :rows="rows"
      :bucket="energyBucket(range)"
      :window="chartWindow"
      :label="label"
    />
  </section>
</template>
