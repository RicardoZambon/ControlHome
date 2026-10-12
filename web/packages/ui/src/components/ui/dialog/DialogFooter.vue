<script setup lang="ts">
import type { HTMLAttributes } from 'vue'
import { DialogClose } from 'reka-ui'
import { cn } from '@controlhome/ui/lib/utils'
import { Button } from '@controlhome/ui/components/ui/button'

const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes['class']
    showCloseButton?: boolean
    /** Text of the close button (`showCloseButton`); pass a translated string. */
    closeLabel?: string
  }>(),
  {
    showCloseButton: false,
    closeLabel: 'Close',
  },
)
</script>

<template>
  <div
    data-slot="dialog-footer"
    :class="cn('flex flex-col-reverse gap-2 sm:flex-row sm:justify-end', props.class)"
  >
    <slot />
    <DialogClose v-if="showCloseButton" as-child>
      <Button variant="outline">{{ closeLabel }}</Button>
    </DialogClose>
  </div>
</template>
