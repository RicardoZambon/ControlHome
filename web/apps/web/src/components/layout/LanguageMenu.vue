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
} from '@controlhome/ui'
import { Languages } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { isAppLocale, setLocale } from '../../i18n'
import { useSessionStore } from '../../stores/session'

const { t, locale } = useI18n()
const session = useSessionStore()

const options = [
  { value: 'en', labelKey: 'locales.en', short: 'EN' },
  { value: 'pt-BR', labelKey: 'locales.ptBR', short: 'PT' },
] as const

/** Applies the language at once; when signed in, also saves it to the profile. */
async function select(value: unknown): Promise<void> {
  if (!isAppLocale(value) || value === locale.value) return
  setLocale(value)
  if (!session.isAuthenticated) return
  try {
    await session.savePreferences({ locale: value })
  } catch {
    toast.warning(t('shell.preferencesNotSaved'))
  }
}
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <Button
        variant="ghost"
        class="gap-1.5 px-2.5 text-muted-foreground hover:text-accent-foreground has-[>svg]:px-2.5"
        :aria-label="t('shell.chooseLanguage')"
        :title="t('shell.chooseLanguage')"
      >
        <Languages />
        <span class="text-xs font-semibold tracking-wide" aria-hidden="true">
          {{ options.find((option) => option.value === locale)?.short }}
        </span>
      </Button>
    </DropdownMenuTrigger>
    <DropdownMenuContent align="end" class="w-52">
      <DropdownMenuLabel class="text-xs font-medium text-muted-foreground">{{
        t('shell.language')
      }}</DropdownMenuLabel>
      <DropdownMenuRadioGroup :model-value="locale" @update:model-value="select">
        <DropdownMenuRadioItem
          v-for="option in options"
          :key="option.value"
          :value="option.value"
          :lang="option.value"
        >
          {{ t(option.labelKey) }}
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
