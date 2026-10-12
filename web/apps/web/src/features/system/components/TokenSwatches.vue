<script setup lang="ts">
import { themeTokenNames, useThemeTokens } from '@controlhome/ui'
import type { CSSProperties } from 'vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * Every color token of tokens.css, read from the stylesheet itself (themeTokenNames), so a new token shows up here
 * without code changes. The swatches paint with the token variables; the values are the live computed ones.
 */
const { t } = useI18n()
const tokens = useThemeTokens()

type Group = 'surfaces' | 'brand' | 'lines' | 'sidebar' | 'states' | 'rooms' | 'effects' | 'base'
const GROUPS: readonly Group[] = [
  'surfaces',
  'brand',
  'lines',
  'sidebar',
  'states',
  'rooms',
  'effects',
  'base',
]
const BRAND = new Set(['primary', 'secondary', 'accent', 'muted', 'destructive'])
const LINES = new Set(['border', 'input', 'ring'])
const EFFECTS = new Set([
  'overlay',
  'shadow-color-subtle',
  'shadow-color',
  'shadow-color-strong',
  'shadow-highlight',
])
const BASE = new Set(['black', 'white'])
const NOT_COLORS = new Set(['radius'])

function groupOf(name: string): Group {
  if (name.startsWith('state-')) return 'states'
  if (name.startsWith('room-')) return 'rooms'
  if (name.startsWith('sidebar')) return 'sidebar'
  if (EFFECTS.has(name)) return 'effects'
  if (LINES.has(name)) return 'lines'
  if (BRAND.has(name)) return 'brand'
  if (BASE.has(name)) return 'base'
  return 'surfaces'
}

const names = new Set(themeTokenNames)
const groups = computed(() =>
  GROUPS.map((group) => ({
    id: group,
    swatches: themeTokenNames
      .filter((name) => !NOT_COLORS.has(name) && !name.endsWith('-foreground') && groupOf(name) === group)
      .map((name) => ({
        name,
        foreground: names.has(`${name}-foreground`) ? `${name}-foreground` : undefined,
        value: tokens.value[name] ?? '',
        foregroundValue: tokens.value[`${name}-foreground`] ?? '',
      })),
  })).filter((group) => group.swatches.length > 0),
)

const paint = (name: string, foreground?: string): CSSProperties => ({
  backgroundColor: `var(--${name})`,
  color: foreground ? `var(--${foreground})` : undefined,
})
</script>

<template>
  <div class="grid gap-8">
    <div v-for="group in groups" :key="group.id" class="space-y-3">
      <h3 class="eyebrow">{{ t(`system.uiKit.groups.${group.id}`) }}</h3>
      <ul class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
        <li
          v-for="swatch in group.swatches"
          :key="swatch.name"
          class="overflow-hidden rounded-xl border bg-card"
        >
          <!-- Translucent effects: shown over the page background and over the foreground. -->
          <div
            v-if="group.id === 'effects'"
            class="relative grid h-20 grid-cols-2"
            :title="t('system.uiKit.translucent')"
          >
            <div class="bg-background" />
            <div class="bg-foreground" />
            <div class="absolute inset-3 rounded-md" :style="paint(swatch.name)" />
          </div>
          <div
            v-else
            class="flex h-20 items-end justify-between p-3"
            :style="paint(swatch.name, swatch.foreground)"
            :title="swatch.foreground ? t('system.uiKit.foregroundPair') : undefined"
          >
            <span v-if="swatch.foreground" class="text-lg font-semibold" aria-hidden="true">{{
              t('system.uiKit.specimen')
            }}</span>
          </div>
          <div class="grid gap-0.5 p-3">
            <!-- Names wrap instead of being cut off on narrow screens. -->
            <code class="text-xs font-semibold break-all">--{{ swatch.name }}</code>
            <span class="font-mono text-[0.68rem] break-all text-muted-foreground">{{ swatch.value }}</span>
            <span v-if="swatch.foreground" class="font-mono text-[0.68rem] break-all text-muted-foreground">
              --{{ swatch.foreground }}
            </span>
          </div>
        </li>
      </ul>
    </div>
  </div>
</template>
