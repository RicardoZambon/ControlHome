<script setup lang="ts">
import { cn } from '@controlhome/ui'
import type { HTMLAttributes } from 'vue'

/**
 * Decorative floor plan (walls, doors, windows and room tints from the room tokens) that draws itself in. Purely
 * illustrative: it shows no data. `variant="outline"` is the empty, dashed version used before a plan exists.
 */
const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes['class']
    variant?: 'full' | 'outline'
    /** Fill class matching the surface behind the drawing (windows are cut out of the walls with it). */
    surfaceClass?: string
  }>(),
  { variant: 'full', surfaceClass: 'fill-background' },
)

// Wall segments (gaps are doorways). pathLength="1" lets one keyframe draw every segment.
const outerWall = 'M40 40H440V320H40Z'
const innerWalls = [
  'M270 40V80',
  'M270 118V150H340',
  'M378 150H440',
  'M40 200H120',
  'M156 200H230',
  'M262 200H300',
  'M200 200V320',
  'M300 150V160',
  'M300 196V320',
]
const doors = [
  { swing: 'M270 80A38 38 0 0 0 232 118', leaf: 'M232 118H270' },
  { swing: 'M156 200A36 36 0 0 1 120 236', leaf: 'M120 200V236' },
  { swing: 'M230 200A32 32 0 0 0 262 232', leaf: 'M262 200V232' },
  { swing: 'M378 150A38 38 0 0 0 340 112', leaf: 'M340 150V112' },
  { swing: 'M300 160A36 36 0 0 1 336 196', leaf: 'M300 196H336' },
]
const windows = [
  { x: 92, y: 36.5, width: 96, height: 7 },
  { x: 318, y: 36.5, width: 84, height: 7 },
  { x: 436.5, y: 206, width: 7, height: 76 },
  { x: 68, y: 316.5, width: 100, height: 7 },
  { x: 36.5, y: 82, width: 7, height: 80 },
]
// Room tints: the room palette, low opacity (the same tokens later color real rooms).
const rooms = [
  { x: 43, y: 43, width: 224, height: 154, class: 'fill-room-5/20' },
  { x: 273, y: 43, width: 164, height: 104, class: 'fill-room-2/25' },
  { x: 43, y: 203, width: 154, height: 114, class: 'fill-room-8/20' },
  { x: 203, y: 203, width: 94, height: 114, class: 'fill-room-1/20' },
  { x: 303, y: 153, width: 134, height: 164, class: 'fill-room-3/20' },
]
</script>

<template>
  <svg
    viewBox="0 0 480 360"
    aria-hidden="true"
    focusable="false"
    :class="cn('h-auto w-full', props.class)"
    fill="none"
    stroke-linecap="square"
  >
    <!-- Dimension lines -->
    <g class="stroke-muted-foreground/50" stroke-width="1">
      <path d="M40 18H440M40 12V24M440 12V24" />
      <path d="M462 40V320M456 40H468M456 320H468" />
    </g>

    <template v-if="variant === 'full'">
      <rect
        v-for="(room, index) in rooms"
        :key="`room-${index}`"
        :x="room.x"
        :y="room.y"
        :width="room.width"
        :height="room.height"
        rx="2"
        :class="
          cn(
            room.class,
            'motion-safe:animate-in motion-safe:fade-in motion-safe:fill-mode-both motion-safe:duration-1000 motion-safe:delay-1000',
          )
        "
      />
    </template>

    <path
      :d="outerWall"
      :pathLength="variant === 'outline' ? undefined : 1"
      stroke-width="6"
      :stroke-dasharray="variant === 'outline' ? '6 10' : undefined"
      :class="
        cn(
          variant === 'outline'
            ? 'stroke-muted-foreground/60'
            : 'stroke-foreground/80 [stroke-dasharray:1] motion-safe:animate-[ch-draw_1.6s_ease-out_both]',
        )
      "
    />

    <template v-if="variant === 'full'">
      <path
        v-for="(wall, index) in innerWalls"
        :key="`wall-${index}`"
        :d="wall"
        pathLength="1"
        stroke-width="3.5"
        class="stroke-foreground/70 [stroke-dasharray:1] motion-safe:animate-[ch-draw_1s_ease-out_0.6s_both]"
      />
      <g
        class="stroke-muted-foreground motion-safe:animate-in motion-safe:fade-in motion-safe:fill-mode-both motion-safe:duration-700 motion-safe:delay-1000"
        stroke-width="1.25"
        stroke-linecap="round"
      >
        <template v-for="(door, index) in doors" :key="`door-${index}`">
          <path :d="door.swing" stroke-dasharray="3 4" />
          <path :d="door.leaf" />
        </template>
      </g>
      <rect
        v-for="(pane, index) in windows"
        :key="`window-${index}`"
        :x="pane.x"
        :y="pane.y"
        :width="pane.width"
        :height="pane.height"
        stroke-width="1.25"
        :class="
          cn(
            surfaceClass,
            'stroke-foreground/60 motion-safe:animate-in motion-safe:fade-in motion-safe:fill-mode-both motion-safe:duration-700 motion-safe:delay-700',
          )
        "
      />
    </template>
  </svg>
</template>
