import { toTypedSchema, type z } from '@controlhome/ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { Translate } from '../lib/problem'

/**
 * A vee-validate schema whose messages follow the UI language: `build` runs again when the language changes.
 * (vee-validate shows the messages as given, so they are translated here.)
 */
export function useLocalizedSchema<TSchema extends z.ZodTypeAny>(build: (t: Translate) => TSchema) {
  const { t, locale } = useI18n()
  return computed(() => {
    // Read the locale explicitly so the schema is rebuilt when it changes.
    void locale.value
    return toTypedSchema(build(t))
  })
}
