<script setup lang="ts">
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  ToggleGroup,
  ToggleGroupItem,
} from '@controlhome/ui'
import { Ruler } from '@lucide/vue'
import { computed, ref, useId, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatLength } from '../../../components/floorplan/measure'
import { parseDecimal } from '../../../lib/validation'

/**
 * Step two of calibration: the real distance between the two points the user clicked on the background image. Emits
 * `apply` with that distance in cm; the page rescales the image (and reports a scale out of range with `error`).
 */
const props = defineProps<{ open: boolean; measuredCm: number; error?: string }>()
const emit = defineEmits<{ 'update:open': [open: boolean]; apply: [realCm: number] }>()

const { t, locale } = useI18n()
const distanceId = useId()
const errorId = useId()

const value = ref('')
const unit = ref<'m' | 'cm'>('m')
const invalid = ref(false)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    invalid.value = false
    // Start from the measured distance, so a nearly right scale needs only a small correction.
    const metres = props.measuredCm / 100
    unit.value = metres >= 1 ? 'm' : 'cm'
    const shown = unit.value === 'm' ? metres : props.measuredCm
    value.value = new Intl.NumberFormat(locale.value, {
      maximumFractionDigits: 2,
      useGrouping: false,
    }).format(shown)
  },
)

const measured = computed(() => formatLength(props.measuredCm, locale.value))

function submit(): void {
  const parsed = parseDecimal(value.value)
  if (parsed === null || !Number.isFinite(parsed) || parsed <= 0) {
    invalid.value = true
    return
  }
  invalid.value = false
  emit('apply', unit.value === 'm' ? parsed * 100 : parsed)
}

function setUnit(next: unknown): void {
  if (next === 'm' || next === 'cm') unit.value = next
}

const message = computed(() => (invalid.value ? t('floors.editor.calibration.invalid') : props.error))
</script>

<template>
  <Dialog :open="open" @update:open="emit('update:open', $event)">
    <DialogContent :close-label="t('common.close')" class="sm:max-w-md">
      <form novalidate class="grid gap-5" @submit.prevent="submit">
        <DialogHeader>
          <div class="mb-1 flex size-10 items-center justify-center rounded-lg bg-state-info/15">
            <Ruler class="size-5 text-state-info" aria-hidden="true" />
          </div>
          <DialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('floors.editor.calibration.title') }}
          </DialogTitle>
          <DialogDescription>
            {{ t('floors.editor.calibration.description', { measured }) }}
          </DialogDescription>
        </DialogHeader>

        <div class="grid gap-2">
          <Label :for="distanceId">{{ t('floors.editor.calibration.distance') }}</Label>
          <div class="flex gap-2">
            <Input
              :id="distanceId"
              v-model="value"
              inputmode="decimal"
              autocomplete="off"
              class="flex-1 tabular-nums"
              :aria-invalid="message ? true : undefined"
              :aria-describedby="message ? errorId : undefined"
            />
            <ToggleGroup
              type="single"
              variant="outline"
              :model-value="unit"
              :aria-label="t('floors.editor.calibration.unit')"
              @update:model-value="setUnit"
            >
              <ToggleGroupItem value="m">{{ t('floors.editor.calibration.meters') }}</ToggleGroupItem>
              <ToggleGroupItem value="cm">{{ t('floors.editor.calibration.centimeters') }}</ToggleGroupItem>
            </ToggleGroup>
          </div>
          <p v-if="message" :id="errorId" role="alert" class="text-sm text-destructive">{{ message }}</p>
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" @click="emit('update:open', false)">
            {{ t('common.cancel') }}
          </Button>
          <Button type="submit">{{ t('floors.editor.calibration.apply') }}</Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
