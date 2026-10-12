<script setup lang="ts">
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
  toast,
  useTheme,
  type ThemeMode,
} from '@controlhome/ui'
import { Monitor, Moon, Sun } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { isThemeMode } from '../../lib/preferences'
import { useSessionStore } from '../../stores/session'

const { t } = useI18n()
const theme = useTheme()
const session = useSessionStore()

const options = [
  { value: 'light', icon: Sun, labelKey: 'theme.light' },
  { value: 'dark', icon: Moon, labelKey: 'theme.dark' },
  { value: 'system', icon: Monitor, labelKey: 'theme.system' },
] as const

// The trigger shows the chosen mode (System included, as on the profile page), not just the theme in effect.
const current = computed(() => options.find((option) => option.value === theme.mode.value) ?? options[2])
const triggerLabel = computed(() => t('shell.themeCurrent', { theme: t(current.value.labelKey) }))

/** Applies the theme at once; when signed in, also saves it to the profile (so it follows the user). */
async function select(value: unknown): Promise<void> {
  if (!isThemeMode(value) || value === theme.mode.value) return
  theme.setMode(value as ThemeMode)
  if (!session.isAuthenticated) return
  try {
    await session.savePreferences({ theme: value })
  } catch {
    toast.warning(t('shell.preferencesNotSaved'))
  }
}
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <Button variant="ghost" size="icon" :aria-label="t('shell.chooseTheme')" :title="triggerLabel">
        <component :is="current.icon" />
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" class="w-44">
      <DropdownMenuLabel class="text-xs font-medium text-muted-foreground">{{
        t('theme.label')
      }}</DropdownMenuLabel>
      <DropdownMenuRadioGroup :model-value="theme.mode.value" @update:model-value="select">
        <DropdownMenuRadioItem v-for="option in options" :key="option.value" :value="option.value">
          <component :is="option.icon" />
          {{ t(option.labelKey) }}
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
