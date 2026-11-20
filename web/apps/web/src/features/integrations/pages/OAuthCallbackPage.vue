<script setup lang="ts">
import { Button, Input } from '@controlhome/ui'
import { CircleCheckBig, Copy } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { handOver, isCallbackUrl } from '../oauthCallback'

/**
 * Where an app created with the ControlHome address sends the browser after the vendor sign-in. Hands the address to
 * the setup wizard open in another tab; when none takes it, shows the address to copy and paste there.
 */
const { t } = useI18n()
const url = window.location.href
const status = ref<'sending' | 'done' | 'manual'>('sending')
const copied = ref(false)

onMounted(async () => {
  if (isCallbackUrl(url) && (await handOver(url))) {
    status.value = 'done'
    window.close()
  } else {
    status.value = 'manual'
  }
})

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(url)
    copied.value = true
  } catch {
    copied.value = false
  }
}

function selectAll(event: FocusEvent): void {
  ;(event.target as HTMLInputElement).select()
}
</script>

<template>
  <div class="w-full max-w-md" data-slot="oauth-callback" :data-status="status">
    <header class="mb-6 space-y-2">
      <h1 class="font-serif text-4xl font-medium tracking-tight">
        {{ t('integrations.oauthCallback.title') }}
      </h1>
    </header>
    <p v-if="status === 'sending'" class="text-muted-foreground" role="status">
      {{ t('integrations.oauthCallback.sending') }}
    </p>
    <div v-else-if="status === 'done'" class="flex items-start gap-3" role="status">
      <CircleCheckBig class="mt-0.5 size-5 shrink-0 text-state-on" aria-hidden="true" />
      <p>{{ t('integrations.oauthCallback.done') }}</p>
    </div>
    <div v-else class="grid gap-4">
      <p class="text-muted-foreground">{{ t('integrations.oauthCallback.manual') }}</p>
      <Input
        :model-value="url"
        readonly
        :aria-label="t('integrations.oauthCallback.address')"
        @focus="selectAll"
      />
      <Button class="justify-self-start" @click="copy">
        <Copy />
        {{ copied ? t('integrations.oauthCallback.copied') : t('integrations.oauthCallback.copy') }}
      </Button>
    </div>
  </div>
</template>
