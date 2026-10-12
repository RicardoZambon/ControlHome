<script setup lang="ts">
import { cn } from '@controlhome/ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { boundsOf, labelPoint, type Point } from './geometry'
import { formatArea, roomAreaM2 } from './measure'
import { roomColorClasses } from './roomColors'
import { roomIcon } from './roomIcons'

export interface PlanRoom {
  name: string
  icon: string
  colorToken: string
  polygon: readonly Point[]
}

/**
 * One room on the plan: its outline filled with the room's color token (translucent, outline in the solid token) and a
 * label at its visual centre with the name and the area in m². Label parts drop out as the room gets small on screen.
 * Sizes are given in screen pixels and divided by `scale`, so labels and strokes look the same at every zoom.
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
    /** Smaller labels for dashboard-size previews. */
    compact?: boolean
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

const px = (value: number) => value / props.scale

const strokeWidth = computed(() => px(props.selected ? 2.5 : props.hovered ? 2 : 1.5))
const dash = computed(() => (props.invalid ? `${px(6)} ${px(4)}` : undefined))

/** Which label parts fit, and how big the name is. */
const layout = computed(() => {
  const widthPx = (bounds.value.maxX - bounds.value.minX) * props.scale
  const heightPx = (bounds.value.maxY - bounds.value.minY) * props.scale
  const nameSize = props.compact ? 11 : 13
  const areaSize = props.compact ? 10 : 11
  if (!label.value || widthPx < 36 || heightPx < 18) return null
  const room = props.room.name.trim()
  // Fraunces averages about 0.55 em per character.
  const fitChars = Math.max(1, Math.floor((widthPx * 0.86) / (nameSize * 0.55)))
  const name = room.length > fitChars ? `${room.slice(0, Math.max(1, fitChars - 1)).trimEnd()}…` : room
  const showArea = heightPx >= (props.compact ? 34 : 40) && widthPx >= 44
  const showIcon = props.showIcon && heightPx >= (props.compact ? 64 : 76) && widthPx >= 44
  const iconSize = props.compact ? 14 : 16
  const gap = 3
  const lines = [
    ...(showIcon ? [{ kind: 'icon' as const, size: iconSize }] : []),
    { kind: 'name' as const, size: nameSize * 1.25 },
    ...(showArea ? [{ kind: 'area' as const, size: areaSize * 1.3 }] : []),
  ]
  const total = lines.reduce((sum, line) => sum + line.size, 0) + gap * (lines.length - 1)
  let y = -total / 2
  const positioned = lines.map((line) => {
    const top = y
    y += line.size + gap
    return { ...line, top, center: top + line.size / 2 }
  })
  return { name, nameSize, areaSize, iconSize, lines: positioned }
})

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
    <!-- Selection halo -->
    <polygon
      v-if="selected"
      :points="points"
      fill="none"
      class="stroke-ring/35"
      :stroke-width="px(8)"
      stroke-linejoin="round"
    />
    <polygon
      :points="points"
      :class="
        cn(
          'transition-[fill-opacity,stroke-width] duration-150',
          selected || hovered ? colors.fillActive : colors.fill,
          invalid ? 'stroke-destructive' : colors.stroke,
          'group-focus-visible/room:stroke-ring',
        )
      "
      :stroke-width="strokeWidth"
      :stroke-dasharray="dash"
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
        class="text-foreground/70"
      />
      <text
        :y="line('name')!.center"
        text-anchor="middle"
        dominant-baseline="central"
        :font-size="layout.nameSize"
        paint-order="stroke"
        stroke-width="2.5"
        stroke-linejoin="round"
        class="fill-foreground stroke-card/50 font-serif font-medium"
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
        stroke-width="2.5"
        stroke-linejoin="round"
        class="fill-muted-foreground stroke-card/50 font-sans tabular-nums"
      >
        {{ area }}
      </text>
    </g>
  </g>
</template>
