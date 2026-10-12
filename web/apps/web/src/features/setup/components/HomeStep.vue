<script setup lang="ts">
import {
  Button,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  useForm,
  z,
} from '@controlhome/ui'
import { LoaderCircle, LocateFixed } from '@lucide/vue'
import { computed, ref, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import SearchableSelect from '../../../components/SearchableSelect.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { SUPPORTED_LOCALES, type AppLocale } from '../../../i18n'
import { formatTimeZoneName } from '../../../lib/format'
import { browserTimeZone, currencyOptions, currencySymbol, timeZoneOptions } from '../../../lib/locale-data'
import {
  HOME_NAME_MAX_LENGTH,
  optionalDecimalField,
  parseDecimal,
  requiredText,
} from '../../../lib/validation'
import type { HomeDraft } from '../setup'
import StepFooter from './StepFooter.vue'

const props = defineProps<{ modelValue: HomeDraft; errors?: Record<string, string> }>()
const emit = defineEmits<{ next: [home: HomeDraft]; back: [home: HomeDraft] }>()
const { t, locale } = useI18n()

const schema = useLocalizedSchema((t) =>
  z
    .object({
      name: requiredText(t, HOME_NAME_MAX_LENGTH),
      timeZoneId: z.string({ required_error: t('validation.timeZone') }).min(1, t('validation.timeZone')),
      latitude: optionalDecimalField(t, { min: -90, max: 90, message: t('validation.latitude') }),
      longitude: optionalDecimalField(t, { min: -180, max: 180, message: t('validation.longitude') }),
      locale: z.enum(SUPPORTED_LOCALES),
      currency: z
        .string({ required_error: t('validation.currency') })
        .regex(/^[A-Z]{3}$/, t('validation.currency')),
      pricePerKwh: optionalDecimalField(t, { min: 0, message: t('validation.nonNegative') }),
    })
    .superRefine((value, context) => {
      // Coordinates come in pairs: flag the empty one.
      const latitude = parseDecimal(value.latitude ?? null)
      const longitude = parseDecimal(value.longitude ?? null)
      if ((latitude === null) !== (longitude === null))
        context.addIssue({
          code: z.ZodIssueCode.custom,
          path: [latitude === null ? 'latitude' : 'longitude'],
          message: t('validation.coordinatesPair'),
        })
    }),
)

const { handleSubmit, setErrors, setFieldValue, values } = useForm({
  validationSchema: schema,
  initialValues: { ...props.modelValue },
})
// Server errors for this step: applied once the fields exist (after mount), and again when they change.
const applyErrors = () => {
  if (props.errors) setErrors(props.errors)
}
onMounted(applyErrors)
watch(() => props.errors, applyErrors)

const timeZones = timeZoneOptions()
const currencies = computed(() => currencyOptions(locale.value))
const symbol = computed(() => currencySymbol(values.currency ?? 'BRL', locale.value))
const deviceTimeZone = browserTimeZone()
// Example values in the user's number format ("0,85" in Portuguese).
const examples = computed(() => {
  const format = (value: number, digits: number) =>
    new Intl.NumberFormat(locale.value, { minimumFractionDigits: digits, useGrouping: false }).format(value)
  return { latitude: format(-23.5505, 4), longitude: format(-46.6333, 4), price: format(0.85, 2) }
})
const localeOptions: readonly { value: AppLocale; labelKey: string }[] = [
  { value: 'en', labelKey: 'locales.en' },
  { value: 'pt-BR', labelKey: 'locales.ptBR' },
]

// "Use my location": browsers expose geolocation only in secure contexts (HTTPS or localhost).
const secureContext = typeof window !== 'undefined' && window.isSecureContext
const geolocationAvailable = typeof navigator !== 'undefined' && 'geolocation' in navigator
const locating = ref(false)
const locationNote = ref<{ text: string; error: boolean } | null>(
  !secureContext
    ? { text: t('setup.home.locationInsecure'), error: false }
    : !geolocationAvailable
      ? { text: t('setup.home.locationUnavailable'), error: false }
      : null,
)

function fillMyLocation(): void {
  if (!secureContext || !geolocationAvailable) return
  locating.value = true
  locationNote.value = null
  navigator.geolocation.getCurrentPosition(
    (position) => {
      // Four decimals (about 10 m) are plenty for sunrise and sunset.
      setFieldValue('latitude', position.coords.latitude.toFixed(4))
      setFieldValue('longitude', position.coords.longitude.toFixed(4))
      locating.value = false
      locationNote.value = { text: t('setup.home.locationFound'), error: false }
    },
    (error) => {
      locating.value = false
      const key =
        error.code === error.PERMISSION_DENIED ? 'setup.home.locationDenied' : 'setup.home.locationFailed'
      locationNote.value = { text: t(key), error: true }
    },
    { enableHighAccuracy: false, timeout: 15_000, maximumAge: 10 * 60_000 },
  )
}

const asDraft = (home: Partial<Record<keyof HomeDraft, unknown>>): HomeDraft => ({
  name: String(home.name ?? ''),
  timeZoneId: String(home.timeZoneId ?? ''),
  latitude: String(home.latitude ?? ''),
  longitude: String(home.longitude ?? ''),
  locale: (home.locale as AppLocale | undefined) ?? props.modelValue.locale,
  currency: String(home.currency ?? ''),
  pricePerKwh: String(home.pricePerKwh ?? ''),
})

const onSubmit = handleSubmit((home) => emit('next', asDraft(home)))
const goBack = () => emit('back', asDraft({ ...props.modelValue, ...values }))
</script>

<template>
  <form novalidate class="grid gap-7" @submit="onSubmit">
    <FormField v-slot="{ componentField }" name="name">
      <FormItem>
        <FormLabel>{{ t('setup.home.name') }}</FormLabel>
        <FormControl>
          <Input v-bind="componentField" :placeholder="t('setup.home.namePlaceholder')" />
        </FormControl>
        <FormMessage />
      </FormItem>
    </FormField>

    <FormField v-slot="{ componentField }" name="timeZoneId">
      <FormItem>
        <FormLabel>{{ t('setup.home.timeZone') }}</FormLabel>
        <FormControl>
          <SearchableSelect
            v-bind="componentField"
            :options="timeZones"
            :label="t('setup.home.timeZone')"
            :placeholder="t('setup.home.timeZone')"
            :search-placeholder="t('setup.home.timeZoneSearch')"
            :empty-text="t('setup.home.timeZoneEmpty')"
          />
        </FormControl>
        <FormDescription v-if="deviceTimeZone && deviceTimeZone !== values.timeZoneId">
          <Button
            type="button"
            variant="link"
            class="h-auto p-0 text-sm font-normal"
            @click="setFieldValue('timeZoneId', deviceTimeZone)"
          >
            {{ t('setup.home.useBrowserTimeZone', { zone: formatTimeZoneName(deviceTimeZone) }) }}
          </Button>
        </FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>

    <fieldset class="grid gap-3">
      <legend class="mb-1 text-sm leading-none font-medium">{{ t('setup.home.location') }}</legend>
      <p class="text-sm text-muted-foreground">{{ t('setup.home.locationHelp') }}</p>
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-start">
        <FormField v-slot="{ componentField }" name="latitude">
          <FormItem>
            <FormLabel class="text-xs text-muted-foreground">{{ t('setup.home.latitude') }}</FormLabel>
            <FormControl>
              <Input
                v-bind="componentField"
                inputmode="decimal"
                autocomplete="off"
                :placeholder="examples.latitude"
              />
            </FormControl>
            <FormMessage />
          </FormItem>
        </FormField>
        <FormField v-slot="{ componentField }" name="longitude">
          <FormItem>
            <FormLabel class="text-xs text-muted-foreground">{{ t('setup.home.longitude') }}</FormLabel>
            <FormControl>
              <Input
                v-bind="componentField"
                inputmode="decimal"
                autocomplete="off"
                :placeholder="examples.longitude"
              />
            </FormControl>
            <FormMessage />
          </FormItem>
        </FormField>
        <Button
          type="button"
          variant="outline"
          class="col-span-2 sm:col-span-1 sm:mt-6"
          :disabled="locating || !secureContext || !geolocationAvailable"
          @click="fillMyLocation"
        >
          <LoaderCircle v-if="locating" class="animate-spin" />
          <LocateFixed v-else />
          {{ locating ? t('setup.home.locating') : t('setup.home.useMyLocation') }}
        </Button>
      </div>
      <p
        v-if="locationNote"
        :class="locationNote.error ? 'text-sm text-destructive' : 'text-sm text-muted-foreground'"
        role="status"
      >
        {{ locationNote.text }}
      </p>
    </fieldset>

    <div class="grid items-start gap-6 sm:grid-cols-2">
      <FormField v-slot="{ componentField }" name="locale">
        <FormItem>
          <FormLabel>{{ t('setup.home.language') }}</FormLabel>
          <Select v-bind="componentField">
            <FormControl>
              <SelectTrigger class="w-full">
                <SelectValue />
              </SelectTrigger>
            </FormControl>
            <SelectContent>
              <SelectItem v-for="option in localeOptions" :key="option.value" :value="option.value">
                {{ t(option.labelKey) }}
              </SelectItem>
            </SelectContent>
          </Select>
          <FormDescription>{{ t('setup.home.languageHelp') }}</FormDescription>
          <FormMessage />
        </FormItem>
      </FormField>
      <FormField v-slot="{ componentField }" name="currency">
        <FormItem>
          <FormLabel>{{ t('setup.home.currency') }}</FormLabel>
          <FormControl>
            <SearchableSelect
              v-bind="componentField"
              :options="currencies"
              :label="t('setup.home.currency')"
              :placeholder="t('setup.home.currency')"
              :search-placeholder="t('setup.home.currencySearch')"
              :empty-text="t('setup.home.currencyEmpty')"
            />
          </FormControl>
          <FormMessage />
        </FormItem>
      </FormField>
    </div>

    <FormField v-slot="{ componentField }" name="pricePerKwh">
      <FormItem class="sm:max-w-xs">
        <FormLabel>
          {{ t('setup.home.price') }}
          <span class="font-normal text-muted-foreground">({{ t('common.optional') }})</span>
        </FormLabel>
        <div class="relative">
          <span
            class="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-sm text-muted-foreground"
            aria-hidden="true"
          >
            {{ symbol }}
          </span>
          <FormControl>
            <Input
              v-bind="componentField"
              inputmode="decimal"
              autocomplete="off"
              :placeholder="examples.price"
              class="pl-11"
            />
          </FormControl>
        </div>
        <FormDescription>{{ t('setup.home.priceHelp') }}</FormDescription>
        <FormMessage />
      </FormItem>
    </FormField>

    <StepFooter can-go-back @back="goBack" />
  </form>
</template>
