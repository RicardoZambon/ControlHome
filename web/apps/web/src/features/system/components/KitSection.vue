<script setup lang="ts">
/** A titled section of the UI kit page (anchor target for the table of contents). */
defineProps<{ id: string; title: string }>()
</script>

<template>
  <section :id="id" :aria-labelledby="`${id}-title`" class="scroll-mt-20 space-y-5 border-t pt-10">
    <h2 :id="`${id}-title`" class="font-serif text-2xl font-medium tracking-tight">{{ title }}</h2>
    <slot />
  </section>
</template>
