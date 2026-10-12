<script setup lang="ts">
import { Alert, AlertDescription, AlertTitle, cn } from '@controlhome/ui'
import { CircleAlert } from '@lucide/vue'
import type { HTMLAttributes } from 'vue'

/** A form-level error (announced to screen readers). Render it only while there is a message. */
const props = defineProps<{ message: string; title?: string; class?: HTMLAttributes['class'] }>()
</script>

<template>
  <Alert
    variant="destructive"
    role="alert"
    :class="cn('motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-top-1', props.class)"
  >
    <CircleAlert />
    <AlertTitle v-if="title">{{ title }}</AlertTitle>
    <AlertDescription>{{ message }}</AlertDescription>
  </Alert>
</template>
