<script setup lang="ts">
import { getAttributeHistory, type HistorySeriesDto } from '@controlhome/api-client'
import { Button, Skeleton } from '@controlhome/ui'
import { RefreshCw } from '@lucide/vue'
import { computed, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import HistoryChart from '../../../components/charts/HistoryChart.vue'
import StateTimeline from '../../../components/charts/StateTimeline.vue'
import {
  rawTailFrom,
  recordingStart,
  windowQuery,
  withRawTail,
  type HistoryRange,
  type TimeWindow,
} from '../../../components/charts/chartOptions'
import {
  attributeLabel,
  capabilityLabel,
  formatMeasurement,
  formatStateValue,
  valueLabel,
} from '../../../components/devices/format'
import { useProblem } from '../../../composables/useProblem'
import { formatClock } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { asNumber } from '../../../lib/stateValue'
import { useRealtimeStore } from '../../../stores/realtime'
import { chartToken, timelineClass, zeroBasedUnit, type RecordedAttribute } from '../history'

/**
 * One recorded attribute over the tab's window (`GET /api/attributes/{id}/history`, resolution picked by the server):
 * its name, its value now, and its history — numbers as a chart that ends on the value now, on/off states as a compact
 * timeline ("On since 06:34"), enum states as steps. Statistics end with their last closed bucket, so a number's raw
 * changes since then are fetched too. The part of the window before the device started recording is shaded and says
 * so. Fetched again whenever the window moves (a new range, the minute refresh).
 */
const props = defineProps<{
  entry: RecordedAttribute
  range: HistoryRange
  /** The window every card of the tab shares (server time). */
  window: TimeWindow
  /** The endpoint's name, for devices with several. */
  endpointName?: string
}>()

const { t, te, locale } = useI18n()
const problem = useProblem()
const realtime = useRealtimeStore()

const series = shallowRef<HistorySeriesDto | null>(null)
const loading = ref(true)
const error = ref<unknown>(null)
let request = 0

async function load(): Promise<void> {
  const current = ++request
  error.value = null
  const id = props.entry.attribute.attributeId
  const window = props.window
  try {
    let result = unwrap(
      await getAttributeHistory({ path: { id }, query: { ...windowQuery(window), resolution: 'auto' } }),
    )
    // Statistics end with the last closed bucket: the raw changes since then complete the chart up to now.
    const tailFrom = props.entry.kind === 'number' ? rawTailFrom(result, window.max) : null
    if (tailFrom) {
      const raw = unwrap(
        await getAttributeHistory({
          path: { id },
          query: { from: tailFrom, to: new Date(window.max).toISOString(), resolution: 'raw' },
        }),
      )
      const merged = withRawTail(result, raw)
      // No bucket closed yet (a device that just started reporting): its raw changes are the whole series.
      result = result.points.length === 0 ? { ...merged, resolution: 'raw' } : merged
    }
    if (current === request) series.value = result
  } catch (failure) {
    if (current === request) error.value = failure
  } finally {
    if (current === request) loading.value = false
  }
}

watch(
  () => [props.window.min, props.window.max, props.entry.attribute.attributeId] as const,
  (next, previous) => {
    // A new range or attribute shows the skeleton; the minute refresh keeps the chart while it fetches.
    if (!previous || next[2] !== previous[2] || next[1] - next[0] !== previous[1] - previous[0])
      loading.value = true
    void load()
  },
  { immediate: true },
)

const capabilityId = computed(() => props.entry.capability.capabilityId)
const key = computed(() => props.entry.attribute.key)
const unit = computed(() => series.value?.unit ?? props.entry.definition.unit)
const title = computed(() => attributeLabel(key.value, t, te, capabilityId.value))
const live = computed(() => realtime.valueOf(props.entry.attribute.attributeId))
const current = computed(() =>
  formatStateValue(live.value, {
    attributeKey: key.value,
    schema: props.entry.definition.schema,
    unit: props.entry.definition.unit,
    locale: locale.value,
    t,
    te,
  }),
)
const enumValues = computed(() =>
  props.entry.definition.schema.type === 'enum' ? props.entry.definition.schema.values : undefined,
)

function onOffLabel(on: boolean): string {
  const words = `device.values.${key.value}.${on}`
  return te(words) ? t(words) : on ? t('schema.true') : t('schema.false')
}

const now = computed(() => new Date(props.window.max))
const clock = (time: number) => formatClock(new Date(time), now.value, locale.value)

/** Where the recording starts inside the window (the part before it has no data). */
const recordedFrom = computed(() => {
  const first = series.value?.points[0]
  return recordingStart(props.window, first ? Date.parse(first.t) : undefined)
})
const noData = computed(() =>
  recordedFrom.value === null
    ? null
    : { until: recordedFrom.value, label: t('history.recordingSince', { time: clock(recordedFrom.value) }) },
)

/** An on/off state's latest stretch: "On since 06:34", or "On the whole time" when it never changed in the window. */
const stateSince = computed(() => {
  const points = series.value?.points ?? []
  const last = [...points].reverse().find((point) => point.v !== null)
  if (!last || last.v === null) return ''
  const state = onOffLabel(last.v > 0)
  const time = Date.parse(last.t)
  return time <= props.window.min
    ? t('history.throughout', { state })
    : t('history.since', { state, time: clock(time) })
})

/** The endpoint, the capability (where it adds something) and how the history is kept or since when it is. */
const subtitle = computed(() => {
  const parts: string[] = []
  if (props.endpointName) parts.push(props.endpointName)
  const capability = capabilityLabel(capabilityId.value, t, te)
  // An on/off card says how long it has been so; the capability's name would only repeat the title.
  if (capability !== title.value && props.entry.kind !== 'bool') parts.push(capability)
  if (props.entry.kind === 'bool') {
    if (stateSince.value) parts.push(stateSince.value)
  } else if (series.value && te(`history.resolution.${series.value.resolution}`)) {
    parts.push(t(`history.resolution.${series.value.resolution}`))
  }
  return parts.join(' · ')
})
const chartLabel = computed(() =>
  t('history.chartLabel', { name: title.value, range: t(`history.ranges.${props.range}`) }),
)
const hasPoints = computed(() => (series.value?.points ?? []).some((point) => point.v !== null))
</script>

<template>
  <section
    class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 rounded-2xl border bg-card p-5 shadow-xs"
    :aria-label="title"
    data-slot="attribute-history"
    :data-attribute-id="entry.attribute.attributeId"
    :data-kind="entry.kind"
  >
    <header class="flex items-start justify-between gap-3">
      <div class="grid min-w-0 gap-0.5">
        <h3 class="truncate font-medium">{{ title }}</h3>
        <p v-if="subtitle" class="truncate text-xs text-muted-foreground" data-slot="history-subtitle">
          {{ subtitle }}
        </p>
      </div>
      <div class="grid shrink-0 justify-items-end gap-0.5 text-right">
        <span class="text-xs text-muted-foreground">{{ t('history.now') }}</span>
        <span
          class="font-serif text-2xl leading-none font-medium tracking-tight tabular-nums"
          data-slot="history-now"
          >{{ current }}</span
        >
      </div>
    </header>

    <Skeleton
      v-if="loading && !series"
      :class="entry.kind === 'bool' ? 'h-16' : 'h-52'"
      class="w-full rounded-xl"
    />
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
    <template v-else-if="series">
      <template v-if="entry.kind === 'bool'">
        <StateTimeline
          v-if="hasPoints"
          :points="series.points"
          :window="window"
          :label-of="onOffLabel"
          :on-class="timelineClass(capabilityId)"
          :label="chartLabel"
        />
        <p
          v-else
          class="grid h-16 place-items-center rounded-xl border border-dashed text-sm text-muted-foreground"
          data-slot="history-empty"
        >
          {{ t('history.empty') }}
        </p>
      </template>
      <HistoryChart
        v-else
        :series="series"
        :kind="entry.kind"
        :window="window"
        :label="chartLabel"
        :token="chartToken(capabilityId, key)"
        :format-value="(value) => formatMeasurement(value, unit, locale)"
        :label-of="(value) => valueLabel(key, value, t, te)"
        :enum-values="enumValues"
        :unit="unit"
        :zero-based="zeroBasedUnit(unit)"
        :live="entry.kind === 'number' ? (asNumber(live) ?? null) : null"
        :no-data="noData"
      />
    </template>
  </section>
</template>
