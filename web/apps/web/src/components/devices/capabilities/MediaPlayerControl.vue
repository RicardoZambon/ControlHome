<script setup lang="ts">
import { Button, cn } from '@controlhome/ui'
import { Pause, Play, SkipBack, SkipForward, Square, Volume1, Volume2, VolumeX } from '@lucide/vue'
import { computed, ref } from 'vue'
import SchemaField from '../../schema/SchemaField.vue'
import FillSlider from '../controls/FillSlider.vue'
import PowerButton from '../controls/PowerButton.vue'
import type { CapabilityControlProps } from './types'
import { useControl } from './useControl'

/**
 * `mediaPlayer` (TVs, speakers): power, what is playing (source and app, which can be changed), the transport buttons,
 * and the volume with mute and steps. Volume changes go out when the slider is released.
 */
const props = defineProps<CapabilityControlProps>()
const control = useControl(props)
const { t } = control

const power = computed(() => control.state.bool('power'))
const volume = computed(() => control.state.number('volume'))
const muted = computed(() => control.state.bool('muted') === true)
const playback = computed(() => control.state.string('playbackState'))
const source = computed(() => control.state.string('source'))
const app = computed(() => control.state.string('app'))
const playing = computed(() => playback.value === 'playing' || playback.value === 'buffering')
const live = computed(() => control.enabled.value && power.value !== false)

const draggedVolume = ref<number>()
const shownVolume = computed(() => draggedVolume.value ?? volume.value)

function togglePower(): void {
  void control.send(power.value ? 'powerOff' : 'powerOn')
}

function setVolume(value: number): void {
  draggedVolume.value = undefined
  void control.send('setVolume', { volume: Math.round(value) })
}

const STRING = { type: 'string', maxLength: 128 } as const
const sourceDraft = ref<string>()
const appDraft = ref<string>()

function select(command: 'selectSource' | 'launchApp', value: unknown): void {
  if (typeof value !== 'string' || value.trim() === '') return
  if (command === 'selectSource') sourceDraft.value = undefined
  else appDraft.value = undefined
  if (value.trim() === (command === 'selectSource' ? source.value : app.value)) return
  void control.send(command, command === 'selectSource' ? { source: value.trim() } : { app: value.trim() })
}
</script>

<template>
  <PowerButton
    v-if="variant === 'tile'"
    :on="power"
    :label="t('device.switch.label', { name: control.name.value })"
    :pending="control.pending('powerOn') || control.pending('powerOff')"
    :disabled="!control.enabled.value"
    @toggle="togglePower"
  />

  <div v-else class="grid gap-6" data-slot="media-control">
    <div class="flex flex-wrap items-center gap-5">
      <PowerButton
        :on="power"
        :label="t('device.switch.label', { name: control.name.value })"
        :pending="control.pending('powerOn') || control.pending('powerOff')"
        :disabled="!control.enabled.value"
        @toggle="togglePower"
      />
      <div class="grid min-w-0 flex-1 gap-0.5">
        <span class="eyebrow">{{ t('device.media.nowPlaying') }}</span>
        <p class="truncate font-serif text-2xl font-medium tracking-tight" aria-live="polite">
          {{
            power === false
              ? t('device.state.off')
              : (app ??
                source ??
                (playback ? control.valueLabel('playbackState', playback) : t('device.noValue')))
          }}
        </p>
        <p v-if="power !== false && playback && (app || source)" class="text-sm text-muted-foreground">
          {{ control.valueLabel('playbackState', playback)
          }}<template v-if="app && source"> · {{ source }}</template>
        </p>
      </div>
    </div>

    <!-- Transport -->
    <div
      class="flex items-center justify-center gap-3"
      role="group"
      :aria-label="t('device.media.transport')"
    >
      <Button
        v-if="control.hasCommand('previous')"
        variant="ghost"
        size="icon-lg"
        class="rounded-full"
        :disabled="!live"
        :aria-label="control.commandLabel('previous')"
        @click="control.send('previous')"
      >
        <SkipBack aria-hidden="true" />
      </Button>
      <Button
        size="icon-lg"
        class="size-16 rounded-full [&_svg]:size-7"
        :disabled="!live"
        :loading="control.pending('play') || control.pending('pause')"
        :aria-label="playing ? control.commandLabel('pause') : control.commandLabel('play')"
        @click="control.send(playing ? 'pause' : 'play')"
      >
        <Pause v-if="playing" aria-hidden="true" />
        <Play v-else aria-hidden="true" class="translate-x-0.5" />
      </Button>
      <Button
        v-if="control.hasCommand('stop')"
        variant="ghost"
        size="icon-lg"
        class="rounded-full"
        :disabled="!live"
        :aria-label="control.commandLabel('stop')"
        @click="control.send('stop')"
      >
        <Square aria-hidden="true" />
      </Button>
      <Button
        v-if="control.hasCommand('next')"
        variant="ghost"
        size="icon-lg"
        class="rounded-full"
        :disabled="!live"
        :aria-label="control.commandLabel('next')"
        @click="control.send('next')"
      >
        <SkipForward aria-hidden="true" />
      </Button>
    </div>

    <!-- Volume -->
    <div v-if="control.state.has('volume')" class="grid gap-2">
      <span class="flex items-center justify-between text-sm text-muted-foreground">
        {{ control.attributeLabel('volume') }}
        <span class="font-medium text-foreground tabular-nums">
          {{
            muted
              ? t('device.media.muted')
              : shownVolume === undefined
                ? t('device.noValue')
                : control.measure(shownVolume, '%')
          }}
        </span>
      </span>
      <div class="flex items-center gap-2">
        <Button
          variant="outline"
          size="icon"
          :disabled="!live"
          :aria-pressed="muted"
          :aria-label="muted ? t('device.media.unmute') : t('device.media.mute')"
          @click="control.send('setMuted', { muted: !muted })"
        >
          <VolumeX v-if="muted" aria-hidden="true" />
          <Volume2 v-else aria-hidden="true" />
        </Button>
        <FillSlider
          :model-value="volume ?? 0"
          :label="t('device.media.volume', { name: control.name.value })"
          :value-text="shownVolume === undefined ? undefined : control.measure(shownVolume, '%')"
          :disabled="!live"
          :fill-class="cn(muted ? 'bg-muted-foreground/40' : 'bg-primary')"
          class="flex-1"
          @update:model-value="(value) => (draggedVolume = value)"
          @commit="setVolume"
        />
        <Button
          v-if="control.hasCommand('volumeDown')"
          variant="outline"
          size="icon"
          :disabled="!live"
          :aria-label="control.commandLabel('volumeDown')"
          @click="control.send('volumeDown')"
        >
          <Volume1 aria-hidden="true" />
        </Button>
        <Button
          v-if="control.hasCommand('volumeUp')"
          variant="outline"
          size="icon"
          :disabled="!live"
          :aria-label="control.commandLabel('volumeUp')"
          @click="control.send('volumeUp')"
        >
          <Volume2 aria-hidden="true" />
        </Button>
      </div>
    </div>

    <!-- Source and app -->
    <div v-if="control.canControl.value" class="grid gap-4 sm:grid-cols-2">
      <SchemaField
        v-if="control.hasCommand('selectSource')"
        :schema="STRING"
        :model-value="sourceDraft ?? source ?? ''"
        :label="control.attributeLabel('source')"
        :disabled="!live"
        @update:model-value="(value) => (sourceDraft = value as string)"
        @commit="(value) => select('selectSource', value)"
      />
      <SchemaField
        v-if="control.hasCommand('launchApp')"
        :schema="STRING"
        :model-value="appDraft ?? app ?? ''"
        :label="control.attributeLabel('app')"
        :disabled="!live"
        @update:model-value="(value) => (appDraft = value as string)"
        @commit="(value) => select('launchApp', value)"
      />
    </div>
  </div>
</template>
