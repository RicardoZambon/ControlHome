<script setup lang="ts">
import { replaceDevice, type DeviceDetailDto } from '@controlhome/api-client'
import {
  Badge,
  Button,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  RadioGroup,
  RadioGroupItem,
  cn,
} from '@controlhome/ui'
import { ArrowRight, Search } from '@lucide/vue'
import { computed, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import DeviceIcon from '../../../components/devices/DeviceIcon.vue'
import { normalizeText } from '../../../components/floorplan/placements'
import { useProblem } from '../../../composables/useProblem'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { replaceCandidates } from '../replace'

/**
 * Admin: picks the device that takes over this one (`POST /api/devices/{id}/replace`): it gets this device's name, room,
 * icon, place on the plan, favorites and history, and this device is deleted. The same hardware seen through another
 * integration is offered first.
 */
const props = defineProps<{ device: DeviceDetailDto }>()
const emit = defineEmits<{ replaced: [device: DeviceDetailDto]; cancel: [] }>()

const { t } = useI18n()
const problem = useProblem()
const devices = useDevicesStore()
const id = useId()

const query = ref('')
const selected = ref('')
const busy = ref(false)
const error = ref('')

const candidates = computed(() => {
  const needle = normalizeText(query.value)
  return replaceCandidates(props.device, devices.devices).filter(
    (entry) => !needle || normalizeText(entry.device.name).includes(needle),
  )
})
const chosen = computed(() => devices.devicesById.get(selected.value))

function integrationName(integrationId: string): string {
  return devices.integrationsById.get(integrationId)?.name ?? ''
}

async function confirm(): Promise<void> {
  if (!selected.value) return
  busy.value = true
  error.value = ''
  try {
    const saved = unwrap(
      await replaceDevice({ path: { id: props.device.id }, body: { newDeviceId: selected.value } }),
    )
    emit('replaced', saved)
  } catch (failure) {
    error.value = problem.message(failure)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="grid gap-5" data-slot="replace-device">
    <DialogHeader>
      <DialogTitle class="font-serif text-2xl font-medium tracking-tight">
        {{ t('devices.replace.title', { name: device.name }) }}
      </DialogTitle>
      <DialogDescription>{{ t('devices.replace.description') }}</DialogDescription>
    </DialogHeader>

    <FormAlert v-if="error" :message="error" />

    <div class="relative">
      <label :for="`${id}-search`" class="sr-only">{{ t('devices.replace.search') }}</label>
      <Search
        class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
        aria-hidden="true"
      />
      <Input
        :id="`${id}-search`"
        v-model="query"
        type="search"
        class="pl-9"
        :placeholder="t('devices.replace.search')"
      />
    </div>

    <p
      v-if="candidates.length === 0"
      class="rounded-xl border border-dashed p-5 text-sm text-muted-foreground"
    >
      {{ t('devices.replace.none') }}
    </p>
    <RadioGroup
      v-else
      v-model="selected"
      class="grid max-h-[min(50dvh,22rem)] gap-1.5 overflow-y-auto pr-1"
      :aria-label="t('devices.replace.pick')"
    >
      <Label
        v-for="entry in candidates"
        :key="entry.device.id"
        :for="`${id}-${entry.device.id}`"
        :class="
          cn(
            'flex cursor-pointer items-center gap-3 rounded-xl border bg-card p-3 font-normal transition-colors hover:border-ring/40',
            selected === entry.device.id && 'border-primary bg-accent/50 ring-1 ring-primary',
          )
        "
        :data-candidate="entry.device.id"
      >
        <RadioGroupItem :id="`${id}-${entry.device.id}`" :value="entry.device.id" />
        <DeviceIcon
          size="sm"
          :icon="entry.device.icon"
          :device-class="entry.device.deviceClass"
          :capabilities="
            entry.device.endpoints.flatMap((endpoint) => endpoint.capabilities.map((c) => c.capabilityId))
          "
        />
        <span class="grid min-w-0 flex-1">
          <span class="truncate text-sm font-medium">{{ entry.device.name }}</span>
          <span class="truncate text-xs text-muted-foreground">{{
            integrationName(entry.device.integrationId)
          }}</span>
        </span>
        <Badge v-if="entry.reason !== 'other'" variant="secondary" class="shrink-0 font-normal">
          {{ t(`devices.replace.reasons.${entry.reason}`) }}
        </Badge>
      </Label>
    </RadioGroup>

    <p
      v-if="chosen"
      class="flex flex-wrap items-center gap-2 rounded-xl bg-muted/50 px-3 py-2.5 text-sm"
      data-slot="replace-summary"
    >
      <span class="font-medium">{{ device.name }}</span>
      <ArrowRight class="size-4 text-muted-foreground" aria-hidden="true" />
      <span class="font-medium">{{ chosen.name }}</span>
      <span class="w-full text-xs text-muted-foreground">{{ t('devices.replace.summary') }}</span>
    </p>

    <DialogFooter>
      <Button type="button" variant="outline" :disabled="busy" @click="emit('cancel')">{{
        t('common.cancel')
      }}</Button>
      <Button :disabled="!selected" :loading="busy" data-slot="replace-confirm" @click="confirm">
        {{ t('devices.replace.confirm') }}
      </Button>
    </DialogFooter>
  </div>
</template>
