<script setup lang="ts">
import { LoaderCircle, Wifi } from '@lucide/vue'
import { onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRealtimeStore } from '../../stores/realtime'

/**
 * Under the top bar: "Reconnecting…" once the live connection has been down for 3 s (device states may be stale
 * meanwhile), then briefly "Live updates are back" when it returns (the state was fetched again).
 */
const { t } = useI18n()
const realtime = useRealtimeStore()

const recovered = ref(false)
let recoveredTimer: ReturnType<typeof setTimeout> | undefined

watch(
  () => realtime.bannerVisible,
  (visible, was) => {
    clearTimeout(recoveredTimer)
    recovered.value = !visible && was && realtime.status === 'connected'
    if (recovered.value) recoveredTimer = setTimeout(() => (recovered.value = false), 3_000)
  },
)

onBeforeUnmount(() => clearTimeout(recoveredTimer))
</script>

<template>
  <div role="status" aria-live="polite" class="sticky top-14 z-10" :data-realtime-status="realtime.status">
    <Transition
      enter-active-class="motion-safe:transition-[opacity,transform] motion-safe:duration-300"
      enter-from-class="opacity-0 -translate-y-1"
      leave-active-class="motion-safe:transition-opacity motion-safe:duration-300"
      leave-to-class="opacity-0"
    >
      <p
        v-if="realtime.bannerVisible"
        class="flex items-center justify-center gap-2 border-b border-state-warn/40 bg-state-warn/15 px-4 py-2 text-center text-sm backdrop-blur-md"
        data-slot="realtime-banner"
      >
        <LoaderCircle class="size-4 shrink-0 text-state-warn motion-safe:animate-spin" aria-hidden="true" />
        <span>
          <span class="font-medium">{{ t('live.reconnecting') }}</span>
          <span class="max-sm:hidden"> {{ t('live.stale') }}</span>
        </span>
      </p>
      <p
        v-else-if="recovered"
        class="flex items-center justify-center gap-2 border-b border-state-on/40 bg-state-on/15 px-4 py-2 text-center text-sm font-medium backdrop-blur-md"
      >
        <Wifi class="size-4 shrink-0 text-state-on" aria-hidden="true" />
        {{ t('live.back') }}
      </p>
    </Transition>
  </div>
</template>
