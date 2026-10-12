<script lang="ts" setup>
import type { CSSProperties } from 'vue'
import type { ToasterProps } from 'vue-sonner'
import { CircleCheckIcon, InfoIcon, Loader2Icon, OctagonXIcon, TriangleAlertIcon, XIcon } from '@lucide/vue'
import { reactiveOmit } from '@vueuse/core'
import { computed } from 'vue'
import { Toaster as Sonner } from 'vue-sonner'
import { useTheme } from '@controlhome/ui/composables/useTheme'

/**
 * Toast host: render once in the app shell, then call `toast(...)` (re-exported from this package).
 * `containerAriaLabel` and `toastOptions.closeButtonAriaLabel` are English by default; pass translated strings.
 */
const props = defineProps<ToasterProps>()

const delegatedProps = reactiveOmit(props, 'class', 'style', 'theme', 'toastOptions')

const { resolved } = useTheme()

// vue-sonner's stylesheet colors toasts through these variables with literal grays; point them at the tokens.
// Inline style on the toaster beats its `[data-sonner-theme]` rules.
const tokenStyle: CSSProperties = {
  '--normal-bg': 'var(--popover)',
  '--normal-bg-hover': 'var(--accent)',
  '--normal-text': 'var(--popover-foreground)',
  '--normal-border': 'var(--border)',
  '--normal-border-hover': 'var(--input)',
  '--gray1': 'var(--popover)',
  '--gray2': 'var(--accent)',
  '--gray3': 'var(--border)',
  '--gray4': 'var(--border)',
  '--gray5': 'var(--input)',
  '--gray11': 'var(--muted-foreground)',
  '--gray12': 'var(--popover-foreground)',
  '--success-bg': 'var(--state-on)',
  '--success-border': 'var(--state-on)',
  '--success-text': 'var(--state-on-foreground)',
  '--info-bg': 'var(--state-info)',
  '--info-border': 'var(--state-info)',
  '--info-text': 'var(--state-info-foreground)',
  '--warning-bg': 'var(--state-warn)',
  '--warning-border': 'var(--state-warn)',
  '--warning-text': 'var(--state-warn-foreground)',
  '--error-bg': 'var(--state-danger)',
  '--error-border': 'var(--state-danger)',
  '--error-text': 'var(--state-danger-foreground)',
  '--border-radius': 'var(--radius)',
}

// Parts that vue-sonner styles with literal colors (shadows, description, buttons, focus). Its stylesheet sits in
// the components layer (styles/index.css), so these utilities win. The close button keeps vue-sonner's
// variable-driven colors, which follow rich (state-colored) toasts.
const focusRing =
  'focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-popover'
// On a state-colored toast (`richColors`) the description uses the state's foreground, like the title.
const richDescription = 'data-[rich-colors=true]:**:data-description:text-current'
const tokenClasses = {
  toast: 'shadow-lg focus-visible:ring-2 focus-visible:ring-ring',
  description: 'text-muted-foreground',
  success: richDescription,
  info: richDescription,
  warning: richDescription,
  error: richDescription,
  actionButton: `bg-primary text-primary-foreground ${focusRing}`,
  cancelButton: `bg-secondary text-secondary-foreground ${focusRing}`,
  closeButton: focusRing,
}

const style = computed<CSSProperties>(() => ({ ...tokenStyle, ...props.style }))
const toastOptions = computed(() => ({
  ...props.toastOptions,
  classes: { ...tokenClasses, ...props.toastOptions?.classes },
}))
</script>

<template>
  <Sonner
    v-bind="delegatedProps"
    :class="props.class"
    :style="style"
    :theme="props.theme ?? resolved"
    :toast-options="toastOptions"
  >
    <template #success-icon>
      <CircleCheckIcon class="size-4" />
    </template>
    <template #info-icon>
      <InfoIcon class="size-4" />
    </template>
    <template #warning-icon>
      <TriangleAlertIcon class="size-4" />
    </template>
    <template #error-icon>
      <OctagonXIcon class="size-4" />
    </template>
    <template #loading-icon>
      <div>
        <Loader2Icon class="size-4 animate-spin" />
      </div>
    </template>
    <template #close-icon>
      <XIcon class="size-4" />
    </template>
  </Sonner>
</template>
