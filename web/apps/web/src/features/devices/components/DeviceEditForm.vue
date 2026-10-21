<script setup lang="ts">
import { updateDevice, type DeviceDetailDto, type RoomListItemDto } from '@controlhome/api-client'
import {
  Button,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  cn,
} from '@controlhome/ui'
import { Sparkles } from '@lucide/vue'
import { computed, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import { DEVICE_ICONS, deviceIcon } from '../../../components/devices/deviceIcons'
import { useProblem } from '../../../composables/useProblem'
import { fieldErrorsOf, unwrap } from '../../../lib/problem'

/** Admin: the device's name, room and icon (`PUT /api/devices/{id}`). Emits the saved device. */
const props = defineProps<{ device: DeviceDetailDto; rooms: readonly RoomListItemDto[] }>()
const emit = defineEmits<{ saved: [device: DeviceDetailDto]; cancel: [] }>()

const NAME_MAX = 128
const NO_ROOM = 'none'

const { t } = useI18n()
const problem = useProblem()
const id = useId()

const name = ref(props.device.name)
const roomId = ref(props.device.roomId === null ? NO_ROOM : String(props.device.roomId))
const icon = ref<string | null>(props.device.icon)
const busy = ref(false)
const nameError = ref('')
const formError = ref('')

const automaticIcon = computed(() =>
  deviceIcon({
    deviceClass: props.device.deviceClass,
    capabilities: props.device.endpoints.flatMap((endpoint) =>
      endpoint.capabilities.map((capability) => capability.capabilityId),
    ),
  }),
)
const multipleFloors = computed(() => new Set(props.rooms.map((room) => room.floorId)).size > 1)

async function save(): Promise<void> {
  nameError.value = ''
  formError.value = ''
  const trimmed = name.value.trim()
  if (!trimmed) nameError.value = t('validation.required')
  else if (trimmed.length > NAME_MAX) nameError.value = t('validation.maxLength', { max: NAME_MAX })
  if (nameError.value) return
  busy.value = true
  try {
    const saved = unwrap(
      await updateDevice({
        path: { id: props.device.id },
        body: {
          name: trimmed,
          icon: icon.value,
          roomId: roomId.value === NO_ROOM ? null : Number(roomId.value),
        },
      }),
    )
    emit('saved', saved)
  } catch (error) {
    const fields = fieldErrorsOf(error)
    if (fields?.name) nameError.value = t('validation.invalid')
    else formError.value = problem.message(error)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form novalidate class="grid gap-6" @submit.prevent="save">
    <DialogHeader>
      <DialogTitle class="font-serif text-2xl font-medium tracking-tight">{{
        t('devices.edit.title')
      }}</DialogTitle>
      <DialogDescription>{{ t('devices.edit.description') }}</DialogDescription>
    </DialogHeader>

    <FormAlert v-if="formError" :message="formError" />

    <div class="grid gap-2">
      <Label :for="`${id}-name`">{{ t('devices.edit.name') }}</Label>
      <Input
        :id="`${id}-name`"
        v-model="name"
        autocomplete="off"
        :maxlength="NAME_MAX"
        :aria-invalid="nameError ? 'true' : undefined"
        :aria-describedby="nameError ? `${id}-name-error` : undefined"
      />
      <p v-if="nameError" :id="`${id}-name-error`" class="text-sm text-destructive">{{ nameError }}</p>
    </div>

    <div class="grid gap-2">
      <Label :for="`${id}-room`">{{ t('devices.edit.room') }}</Label>
      <Select v-model="roomId">
        <SelectTrigger :id="`${id}-room`" class="w-full"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem :value="NO_ROOM">{{ t('device.noRoom') }}</SelectItem>
          <SelectItem v-for="room in rooms" :key="room.id" :value="String(room.id)">
            {{ multipleFloors ? `${room.name} · ${room.floorName}` : room.name }}
          </SelectItem>
        </SelectContent>
      </Select>
    </div>

    <fieldset class="grid gap-3">
      <legend class="mb-3 text-sm leading-none font-medium">{{ t('devices.edit.icon') }}</legend>
      <div
        class="grid grid-cols-6 gap-2 sm:grid-cols-8"
        role="radiogroup"
        :aria-label="t('devices.edit.icon')"
      >
        <button
          type="button"
          role="radio"
          :aria-checked="icon === null"
          :aria-label="t('devices.edit.iconAutomatic')"
          :class="
            cn(
              'relative inline-flex aspect-square items-center justify-center rounded-lg border bg-card text-muted-foreground transition-colors outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background [&_svg]:size-5',
              icon === null && 'border-primary bg-accent text-accent-foreground ring-1 ring-primary',
            )
          "
          @click="icon = null"
        >
          <component :is="automaticIcon" aria-hidden="true" />
          <Sparkles class="absolute top-1 right-1 size-3!" aria-hidden="true" />
        </button>
        <button
          v-for="option in DEVICE_ICONS"
          :key="option.key"
          type="button"
          role="radio"
          :aria-checked="icon === option.key"
          :aria-label="t(`devices.icons.${option.key}`)"
          :title="t(`devices.icons.${option.key}`)"
          :class="
            cn(
              'inline-flex aspect-square items-center justify-center rounded-lg border bg-card text-muted-foreground transition-colors outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background [&_svg]:size-5',
              icon === option.key && 'border-primary bg-accent text-accent-foreground ring-1 ring-primary',
            )
          "
          @click="icon = option.key"
        >
          <component :is="option.icon" aria-hidden="true" />
        </button>
      </div>
    </fieldset>

    <DialogFooter>
      <Button type="button" variant="outline" :disabled="busy" @click="emit('cancel')">{{
        t('common.cancel')
      }}</Button>
      <Button type="submit" :loading="busy">{{ busy ? t('common.saving') : t('common.save') }}</Button>
    </DialogFooter>
  </form>
</template>
