import { toast } from '@controlhome/ui'
import { useI18n } from 'vue-i18n'
import { applyFieldErrors, errorCodeOf, toErrorMessage } from '../lib/problem'

/** Translated API error messages for components (`errors.<code>`), as text, toasts or form field errors. */
export function useProblem() {
  const { t, te } = useI18n()

  const message = (error: unknown): string => toErrorMessage(error, t, (key) => te(key))

  function toastError(error: unknown): void {
    toast.error(message(error))
  }

  /**
   * Puts `validation.failed` field errors on a vee-validate form (`fields`: server path → form field). The message is
   * `fieldMessages[field]` when given, else a generic "isn't valid". Returns whether any field got an error.
   */
  function applyToForm<TField extends string>(
    error: unknown,
    fields: Record<string, TField>,
    setFieldError: (field: TField, message: string) => void,
    fieldMessages: Partial<Record<TField, string>> = {},
  ): boolean {
    return applyFieldErrors(
      error,
      fields,
      setFieldError,
      (field) => fieldMessages[field] ?? t('validation.invalid'),
    )
  }

  return { message, toastError, applyToForm, codeOf: errorCodeOf }
}
