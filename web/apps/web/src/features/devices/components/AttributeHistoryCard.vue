<script setup lang="ts">
import { getAttributeHistory, type HistorySeriesDto } from '@controlhome/api-client'
import { Button, Skeleton } from '@controlhome/ui'
import { RefreshCw } from '@lucide/vue'
import { computed, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import HistoryChart from '../../../components/charts/HistoryChart.vue'
import { rangeQuery, timeWindow, type HistoryRange } from '../../../components/charts/chartOptions'
import {
  attributeLabel,
  capabilityLabel,
  formatMeasurement,
  formatStateValue,
  valueLabel,
} from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useRealtimeStore } from '../../../stores/realtime'
import { chartToken, type RecordedAttribute } from '../history'

/**
 * One recorded attribute over the chosen range: its name, its value now, and its history (`GET
 * /api/attributes/{id}/history`, resolution picked by the server). Fetched again when the range or `refresh` changes.
 */
const props = defineProps<{
  entry: RecordedAttribute
  range: HistoryRange
  /** Bumped to fetch again (every minute while the tab is open). */
  refresh: number
  /** The endpoint's name, for devices with several. */
  endpointName?: string
}>()

const { t, te, locale } = useI18n()
const problem = useProblem()
const realtime = useRealtimeStore()

const series = shallowRef<HistorySeriesDto | null>(null)
const loading = ref(true)
const error = ref<unknown>(null)
const fetchedAt = ref(Date.now())

async function load(): Promise<void> {
  error.value = null
  const now = Date.now()
  try {
    const result = unwrap(
      await getAttributeHistory({
        path: { id: props.entry.attribute.attributeId },
        query: { ...rangeQuery(props.range, now), resolution: 'auto' },
      }),
    )
    series.value = result
    fetchedAt.value = now
  } catch (failure) {
    error.value = failure
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.range, props.entry.attribute.attributeId] as const,
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

const capabilityId = computed(() => props.entry.capability.capabilityId)
const key = computed(() => props.entry.attribute.key)
const unit = computed(() => series.value?.unit ?? props.entry.definition.unit)
const title = computed(() => attributeLabel(key.value, t, te, capabilityId.value))
/** The endpoint (several controls) and the capability, where they add something ("Climate" under "Mode"). */
const subtitle = computed(() => {
  const parts: string[] = []
  if (props.endpointName) parts.push(props.endpointName)
  const capability = capabilityLabel(capabilityId.value, t, te)
  if (capability !== title.value) parts.push(capability)
  return parts.join(' · ')
})
const current = computed(() =>
  formatStateValue(realtime.valueOf(props.entry.attribute.attributeId), {
    attributeKey: key.value,
    schema: props.entry.definition.schema,
    unit: props.entry.definition.unit,
    locale: locale.value,
    t,
    te,
  }),
)
const newest = computed(() => {
  const points = series.value?.points ?? []
  const last = points[points.length - 1]
  return last ? Date.parse(last.t) : undefined
})
const chartWindow = computed(() => timeWindow(props.range, fetchedAt.value, newest.value))
const enumValues = computed(() =>
  props.entry.definition.schema.type === 'enum' ? props.entry.definition.schema.values : undefined,
)

function labelOf(value: string): string {
  if (props.entry.kind === 'bool') {
    const state = value === '1'
    const words = `device.values.${key.value}.${state}`
    return te(words) ? t(words) : state ? t('schema.true') : t('schema.false')
  }
  return valueLabel(key.value, value, t, te)
}

const resolution = computed(() =>
  series.value && te(`history.resolution.${series.value.resolution}`)
    ? t(`history.resolution.${series.value.resolution}`)
    : '',
)
const chartLabel = computed(() =>
  t('history.chartLabel', { name: title.value, range: t(`history.ranges.${props.range}`) }),
)
</script>

<template>
  <section
    class="grid gap-4 rounded-2xl border bg-card p-5 shadow-xs"
    :aria-label="title"
    data-slot="attribute-history"
    :data-attribute-id="entry.attribute.attributeId"
  >
    <header class="flex items-start justify-between gap-3">
      <div class="grid min-w-0 gap-0.5">
        <h3 class="truncate font-medium">{{ title }}</h3>
        <p v-if="subtitle" class="truncate text-xs text-muted-foreground">{{ subtitle }}</p>
      </div>
      <div class="grid shrink-0 justify-items-end gap-0.5 text-right">
        <span class="font-serif text-2xl leading-none font-medium tracking-tight tabular-nums">{{
          current
        }}</span>
        <span v-if="resolution" class="text-xs text-muted-foreground">{{ resolution }}</span>
      </div>
    </header>

    <Skeleton v-if="loading && !series" class="h-52 w-full rounded-xl" />
    <div
      v-else-if="error && !series"
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
    <HistoryChart
      v-else-if="series"
      :series="series"
      :kind="entry.kind"
      :window="chartWindow"
      :label="chartLabel"
      :token="chartToken(capabilityId, key)"
      :format-value="(value) => formatMeasurement(value, unit, locale)"
      :label-of="labelOf"
      :enum-values="enumValues"
    />
  </section>
</template>
