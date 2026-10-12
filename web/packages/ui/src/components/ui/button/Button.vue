<script setup lang="ts">
import type { PrimitiveProps } from 'reka-ui'
import type { HTMLAttributes } from 'vue'
import type { ButtonVariants } from '.'
import { LoaderCircle } from '@lucide/vue'
import { Primitive } from 'reka-ui'
import { cn } from '@controlhome/ui/lib/utils'
import { buttonVariants } from '.'

interface Props extends PrimitiveProps {
  variant?: ButtonVariants['variant']
  size?: ButtonVariants['size']
  class?: HTMLAttributes['class']
  disabled?: boolean
  /**
   * Busy, not disabled: the button keeps its variant's full colors (its label stays readable), shows a spinner in
   * place of its icons, sets `aria-busy` and ignores clicks and form submission until the work is done.
   */
  loading?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  as: 'button',
})
</script>

<template>
  <Primitive
    data-slot="button"
    :data-variant="variant"
    :data-size="size"
    :data-loading="loading || undefined"
    :as="as"
    :as-child="asChild"
    :disabled="disabled || loading || undefined"
    :aria-busy="loading || undefined"
    :class="
      cn(
        buttonVariants({ variant, size }),
        loading && `disabled:opacity-100 [&>svg:not([data-slot='button-spinner'])]:hidden`,
        props.class,
      )
    "
  >
    <LoaderCircle v-if="loading" data-slot="button-spinner" class="animate-spin" aria-hidden="true" />
    <slot />
  </Primitive>
</template>
