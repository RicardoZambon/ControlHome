<script setup lang="ts">
import type { HistoryPointDto } from '@controlhome/api-client'
import { cn } from '@controlhome/ui'
import { computed, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { axisTimeFormatter, stateSegments, type TimeWindow } from './chartOptions'

/**
 * An on/off history as one compact bar over the tab's window: the stretches it was on filled in its state color, off in
 * a quiet tone, and the time before it started recording hatched — with the window's times under it. Each stretch says
 * what it was and when on hover; screen readers get the same as a list.
 */
const props = defineProps<{
  points: readonly Pick<HistoryPointDto, 't' | 'v'>[]
  window: TimeWindow
  /** "On" / "Off" (or "Open" / "Closed"…) for `true` / `false`. */
  labelOf: (on: boolean) => string
  /** Classes of an "on" stretch (`bg-state-on`). */
  onClass?: string
  /** Accessible name ("Power, last 24 hours"). */
  label: string
  class?: HTMLAttributes['class']
}>()

const { t, locale } = useI18n()

const span = computed(() => Math.max(1, props.window.max - props.window.min))
const withDate = computed(() => span.value > 24 * 3_600_000)
const formats = computed(() => ({
  time: new Intl.DateTimeFormat(locale.value, {
    ...(withDate.value ? { day: 'numeric', month: 'short' } : {}),
    hour: '2-digit',
    minute: '2-digit',
  }),
  axis: axisTimeFormatter(locale.value),
}))

const segments = computed(() =>
  stateSegments(props.points, props.window).map((segment) => {
    const range = `${formats.value.time.format(segment.start)}–${formats.value.time.format(segment.end)}`
    const what = segment.on === null ? t('history.notRecorded') : props.labelOf(segment.on)
    return {
      ...segment,
      left: ((segment.start - props.window.min) / span.value) * 100,
      width: ((segment.end - segment.start) / span.value) * 100,
      text: `${what} · ${range}`,
    }
  }),
)

/**
 * Where a stretch sits on the bar. The last one is "now": it always shows, at least a sliver at the right edge, even when
 * the state changed a moment ago.
 */
function styleOf(segment: { left: number; width: number }, last: boolean): Record<string, string> {
  if (!last) return { left: `${segment.left}%`, width: `${segment.width}%` }
  return { right: '0', width: `max(${segment.width}%, 0.5rem)` }
}

/** Five times along the bar: its start, every quarter, and now. */
const ticks = computed(() =>
  [0, 0.25, 0.5, 0.75, 1].map((fraction) => ({
    fraction,
    text: formats.value.axis(props.window.min + fraction * span.value, withDate.value),
  })),
)
</script>

<template>
  <figure
    :class="cn('grid gap-1.5', props.class)"
    data-slot="state-timeline"
    :data-segments="segments.length"
  >
    <div class="relative h-9 overflow-hidden rounded-lg border bg-muted/40" role="img" :aria-label="label">
      <span
        v-for="(segment, index) in segments"
        :key="segment.start"
        :class="
          cn(
            'absolute inset-y-0',
            segment.on === true && (onClass ?? 'bg-state-on'),
            segment.on === false && 'bg-muted',
            segment.on === null && 'bg-hatch',
          )
        "
        :style="styleOf(segment, index === segments.length - 1)"
        :title="segment.text"
        :data-state="segment.on === null ? 'none' : segment.on ? 'on' : 'off'"
      />
    </div>
    <div class="relative h-4 text-xs text-muted-foreground tabular-nums" aria-hidden="true">
      <span
        v-for="entry in ticks"
        :key="entry.fraction"
        :class="
          cn(
            'absolute top-0 whitespace-nowrap',
            entry.fraction === 0 ? 'left-0' : entry.fraction === 1 ? 'right-0' : '-translate-x-1/2',
            entry.fraction > 0 && entry.fraction < 1 && 'max-sm:[&:nth-child(even)]:hidden',
          )
        "
        :style="entry.fraction > 0 && entry.fraction < 1 ? { left: `${entry.fraction * 100}%` } : undefined"
      >
        {{ entry.fraction === 1 ? t('history.now') : entry.text }}
      </span>
    </div>
    <figcaption class="sr-only">
      <ul>
        <li v-for="segment in segments" :key="segment.start">{{ segment.text }}</li>
      </ul>
    </figcaption>
  </figure>
</template>
