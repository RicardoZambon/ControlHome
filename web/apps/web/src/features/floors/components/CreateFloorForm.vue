<script setup lang="ts">
import { createFloor, type FloorDto } from '@controlhome/api-client'
import {
  Button,
  DialogClose,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  useForm,
  z,
} from '@controlhome/ui'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import { NAME_MAX_LENGTH } from '../../../components/floorplan/geometry'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { useProblem } from '../../../composables/useProblem'
import { unwrap, type Translate } from '../../../lib/problem'
import { parseDecimal, requiredText } from '../../../lib/validation'
import { FLOOR_LIMITS } from '../editor/draft'

/** Creates a floor (name, level, outside size in metres), in a dialog. The editor opens next. */
const props = defineProps<{ suggestedName: string; suggestedLevel: number }>()
const emit = defineEmits<{ created: [floor: FloorDto] }>()
const { t } = useI18n()
const problem = useProblem()

function decimalIn(t: Translate, min: number, max: number, integer = false) {
  const range = t('floors.validation.range', { min, max })
  return z.union([z.string(), z.number()]).superRefine((value, context) => {
    const number = parseDecimal(value)
    if (number === null) context.addIssue({ code: z.ZodIssueCode.custom, message: t('validation.required') })
    else if (Number.isNaN(number))
      context.addIssue({ code: z.ZodIssueCode.custom, message: t('validation.number') })
    else if (number < min || number > max || (integer && !Number.isInteger(number)))
      context.addIssue({ code: z.ZodIssueCode.custom, message: range })
  })
}

const schema = useLocalizedSchema((t) =>
  z.object({
    name: requiredText(t, NAME_MAX_LENGTH),
    level: decimalIn(t, FLOOR_LIMITS.minLevel, FLOOR_LIMITS.maxLevel, true),
    width: decimalIn(t, FLOOR_LIMITS.minSizeCm / 100, FLOOR_LIMITS.maxSizeCm / 100),
    height: decimalIn(t, FLOOR_LIMITS.minSizeCm / 100, FLOOR_LIMITS.maxSizeCm / 100),
  }),
)

const { handleSubmit, isSubmitting, setFieldError } = useForm({
  validationSchema: schema,
  initialValues: {
    name: props.suggestedName,
    level: String(props.suggestedLevel),
    width: '20',
    height: '15',
  },
})

const formError = ref('')

const onSubmit = handleSubmit(async (values) => {
  formError.value = ''
  try {
    const floor = unwrap(
      await createFloor({
        body: {
          name: values.name,
          level: Number(parseDecimal(values.level)),
          widthCm: Math.round(Number(parseDecimal(values.width)) * 100),
          heightCm: Math.round(Number(parseDecimal(values.height)) * 100),
        },
      }),
    )
    emit('created', floor)
  } catch (error) {
    const fields = { name: 'name', level: 'level', widthCm: 'width', heightCm: 'height' } as const
    if (!problem.applyToForm(error, fields, setFieldError)) formError.value = problem.message(error)
  }
})
</script>

<template>
  <form novalidate class="grid gap-6" @submit="onSubmit">
    <DialogHeader>
      <DialogTitle class="font-serif text-2xl font-medium tracking-tight">
        {{ t('floors.form.createTitle') }}
      </DialogTitle>
      <DialogDescription>{{ t('floors.form.createDescription') }}</DialogDescription>
    </DialogHeader>

    <FormAlert v-if="formError" :message="formError" />

    <div class="grid gap-5">
      <FormField v-slot="{ componentField }" name="name">
        <FormItem>
          <FormLabel>{{ t('floors.form.name') }}</FormLabel>
          <FormControl>
            <Input
              v-bind="componentField"
              autocomplete="off"
              :placeholder="t('floors.form.namePlaceholder')"
            />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>

      <FormField v-slot="{ componentField }" name="level">
        <FormItem>
          <FormLabel>{{ t('floors.form.level') }}</FormLabel>
          <FormControl>
            <Input v-bind="componentField" inputmode="numeric" autocomplete="off" class="tabular-nums" />
          </FormControl>
          <FormDescription>{{ t('floors.form.levelHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>

      <div class="grid gap-2">
        <div class="grid grid-cols-2 gap-3">
          <FormField v-slot="{ componentField }" name="width">
            <FormItem>
              <FormLabel>{{ t('floors.form.width') }}</FormLabel>
              <FormControl>
                <Input v-bind="componentField" inputmode="decimal" autocomplete="off" class="tabular-nums" />
              </FormControl>
              <FormMessage />
            </FormItem>
          </FormField>
          <FormField v-slot="{ componentField }" name="height">
            <FormItem>
              <FormLabel>{{ t('floors.form.height') }}</FormLabel>
              <FormControl>
                <Input v-bind="componentField" inputmode="decimal" autocomplete="off" class="tabular-nums" />
              </FormControl>
              <FormMessage />
            </FormItem>
          </FormField>
        </div>
        <p class="text-sm text-muted-foreground">{{ t('floors.form.sizeHelp') }}</p>
      </div>
    </div>

    <DialogFooter>
      <DialogClose as-child>
        <Button type="button" variant="outline">{{ t('common.cancel') }}</Button>
      </DialogClose>
      <Button type="submit" :loading="isSubmitting">
        {{ isSubmitting ? t('floors.form.creating') : t('floors.form.create') }}
      </Button>
    </DialogFooter>
  </form>
</template>
