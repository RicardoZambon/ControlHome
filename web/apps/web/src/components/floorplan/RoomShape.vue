<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { boundsOf, labelPoint, type Point } from './geometry'
import { formatArea, roomAreaM2 } from './measure'
import { roomColorClasses } from './roomColors'
import { roomIcon } from './roomIcons'
import { roomLabelLayout } from './roomLabel'

export interface PlanRoom {
  name: string
  icon: string
  colorToken: string
  polygon: readonly Point[]
}

/**
 * One room on the plan: its outline filled with the room's color token (translucent, outline in the solid token) and a
 * label at its visual centre with the name and the area in m², sized to the room on screen (see `roomLabelLayout`).
 * Strokes are screen pixels at every zoom (`vector-effect: non-scaling-stroke`); labels are drawn in screen pixels too.
 * Hover is a small step up in fill; the selection outline is drawn by the canvas, above every room.
 */
const props = withDefaults(
  defineProps<{
    room: PlanRoom
    roomKey: string
    /** Screen px per cm. */
    scale: number
    selected?: boolean
    hovered?: boolean
    /** Fails validation: dashed outline in the danger color. */
    invalid?: boolean
    /** Keyboard focusable, announced as a button (activated with Enter or Space). */
    focusable?: boolean
    /** Previews: small names, no area, a slightly stronger fill. */
    compact?: boolean
    /** An opaque-ish card under the fill, so the drafting grid fades inside rooms (view mode). */
    underlay?: boolean
    showIcon?: boolean
  }>(),
  { showIcon: true },
)
const emit = defineEmits<{ activate: [] }>()
const { t, locale } = useI18n()

const colors = computed(() => roomColorClasses(props.room.colorToken))
const points = computed(() => props.room.polygon.map((p) => `${p.x},${p.y}`).join(' '))
const area = computed(() => formatArea(roomAreaM2(props.room.polygon), locale.value))
const label = computed(() => (props.room.polygon.length >= 3 ? labelPoint(props.room.polygon) : null))
const bounds = computed(() => boundsOf(props.room.polygon))

const fill = computed(() => {
  if (props.compact) return colors.value.fillStrong
  return props.selected || props.hovered ? colors.value.fillHover : colors.value.fill
})

const layout = computed(() =>
  label.value
    ? roomLabelLayout({
        name: props.room.name,
        widthPx: (bounds.value.maxX - bounds.value.minX) * props.scale,
        heightPx: (bounds.value.maxY - bounds.value.minY) * props.scale,
        compact: props.compact,
        showIcon: props.showIcon,
      })
    : null,
)

const line = (kind: 'icon' | 'name' | 'area') => layout.value?.lines.find((entry) => entry.kind === kind)
const Icon = computed(() => roomIcon(props.room.icon))

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    emit('activate')
  }
}
</script>

<template>
  <g
    :data-room-key="roomKey"
    :role="focusable ? 'button' : undefined"
    :tabindex="focusable ? 0 : undefined"
    :aria-label="focusable ? t('floorplan.roomLabel', { name: room.name, area }) : undefined"
    :aria-pressed="focusable ? selected : undefined"
    class="group/room outline-none"
    @keydown="focusable ? onKeydown($event) : undefined"
  >
    <polygon v-if="underlay" :points="points" class="fill-card/85" />
    <polygon
      :points="points"
      :class="
        cn(
          'transition-[fill] duration-150 motion-reduce:transition-none',
          fill,
          invalid ? 'stroke-destructive' : colors.stroke,
          'group-focus-visible/room:stroke-ring',
        )
      "
      :stroke-width="compact ? 1 : 1.5"
      :stroke-dasharray="invalid ? '6 4' : undefined"
      vector-effect="non-scaling-stroke"
      stroke-linejoin="round"
    />
    <g
      v-if="layout && label"
      :transform="`translate(${label.x} ${label.y}) scale(${1 / scale})`"
      class="pointer-events-none"
      aria-hidden="true"
    >
      <component
        :is="Icon"
        v-if="line('icon')"
        :x="-layout.iconSize / 2"
        :y="line('icon')!.top"
        :size="layout.iconSize"
        :stroke-width="1.75"
        class="text-foreground/75"
      />
      <text
        :y="line('name')!.center"
        text-anchor="middle"
        dominant-baseline="central"
        :font-size="layout.nameSize"
        paint-order="stroke"
        stroke-width="3"
        stroke-linejoin="round"
        class="fill-foreground stroke-card/85 font-serif font-medium"
      >
        {{ layout.name }}
      </text>
      <text
        v-if="line('area')"
        :y="line('area')!.center"
        text-anchor="middle"
        dominant-baseline="central"
        :font-size="layout.areaSize"
        paint-order="stroke"
        stroke-width="3"
        stroke-linejoin="round"
        class="fill-foreground/75 stroke-card/85 font-sans tabular-nums"
      >
        {{ area }}
      </text>
    </g>
  </g>
</template>
