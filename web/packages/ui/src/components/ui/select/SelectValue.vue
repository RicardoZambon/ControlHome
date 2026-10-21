<script setup lang="ts">
import type { SelectValueProps } from 'reka-ui'
import { SelectValue } from 'reka-ui'

const props = defineProps<SelectValueProps>()
</script>

<template>
  <SelectValue v-slot="{ selectedLabel }" data-slot="select-value" v-bind="props">
    <!-- A label wider than the trigger ends in "…" instead of being cut off mid-letter. -->
    <slot>
      <span class="min-w-0 truncate">{{
        selectedLabel.length ? selectedLabel.join(', ') : props.placeholder
      }}</span>
    </slot>
  </SelectValue>
</template>
