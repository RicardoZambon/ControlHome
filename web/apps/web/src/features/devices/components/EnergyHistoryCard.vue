<script setup lang="ts">
import { listEnergyHourly, type EndpointDto, type EnergyHourlyDto } from '@controlhome/api-client'
import { Button, Skeleton } from '@controlhome/ui'
import { RefreshCw, Zap } from '@lucide/vue'
import { computed, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import EnergyBars from '../../../components/charts/EnergyBars.vue'
import {
  energyBucket,
  energySlots,
  totalWh,
  type HistoryRange,
  type TimeWindow,
} from '../../../components/charts/chartOptions'
import { formatMeasurement } from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'

/**
 * The energy a device used over the chosen range (`GET /api/energy/hourly` for each of its endpoints that has an energy
 * meter or power readings): the total ("13 Wh in 24 h"), and a bar for every hour (a day) or day (a week, a month) up to
 * the current one.
 */
const props = defineProps<{
  endpoints: readonly EndpointDto[]
  range: HistoryRange
  /** The window every card of the tab shares (server time): the bars end with the hour (day) its end is in. */
  window: TimeWindow
}>()

const { t, locale } = useI18n()
const problem = useProblem()

const rows = shallowRef<EnergyHourlyDto[] | null>(null)
const loading = ref(true)
const error = ref<unknown>(null)
let request = 0

async function load(): Promise<void> {
  const current = ++request
  error.value = null
  const slots = energySlots(props.range, props.window.max)
  const query = { from: new Date(slots[0]!).toISOString(), to: new Date(props.window.max).toISOString() }
  try {
    const lists = await Promise.all(
      props.endpoints.map(async (endpoint) =>
        unwrap(await listEnergyHourly({ query: { ...query, endpointId: endpoint.id } })),
      ),
    )
    if (current === request) rows.value = lists.flat()
  } catch (failure) {
    if (current === request) error.value = failure
  } finally {
    if (current === request) loading.value = false
  }
}

watch(
  () => [props.range, props.window.max, props.endpoints.map((endpoint) => endpoint.id).join()] as const,
  (next, previous) => {
    if (!previous || next[0] !== previous[0] || next[2] !== previous[2]) loading.value = true
    void load()
  },
  { immediate: true },
)

const total = computed(() =>
  t('history.energyTotal', {
    amount: formatMeasurement(totalWh(rows.value ?? []), 'Wh', locale.value),
    range: t(`history.rangesShort.${props.range}`),
  }),
)
const estimated = computed(() => (rows.value ?? []).some((row) => row.estimated))
const label = computed(() => t('history.energyLabel', { range: t(`history.ranges.${props.range}`) }))
</script>

<template>
  <section
    class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 rounded-2xl border bg-card p-5 shadow-xs"
    :aria-label="t('history.energy')"
    data-slot="energy-history"
  >
    <!-- The total wraps under the title on narrow cards rather than cutting it short -->
    <header class="flex flex-wrap items-start justify-between gap-x-3 gap-y-2">
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
      <div class="ml-auto grid shrink-0 justify-items-end gap-0.5 text-right">
        <span
          class="font-serif text-2xl leading-none font-medium tracking-tight tabular-nums"
          data-slot="energy-total"
        >
          {{ rows && rows.length > 0 ? total : '—' }}
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
    <EnergyBars v-else-if="rows" :rows="rows" :range="range" :now="window.max" :label="label" />
  </section>
</template>
