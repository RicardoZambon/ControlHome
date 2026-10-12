import { z } from '@controlhome/ui'
import type { Translate } from './problem'

// Mirrors ControlHome.Application/Common/ValidationRules.cs, so most mistakes are caught before a request.
export const PASSWORD_MIN_LENGTH = 10
export const PASSWORD_MAX_LENGTH = 256
export const USER_NAME_MAX_LENGTH = 64
export const DISPLAY_NAME_MAX_LENGTH = 80
export const HOME_NAME_MAX_LENGTH = 80
const USER_NAME_PATTERN = /^[A-Za-z0-9._@-]{3,64}$/

export function requiredText(t: Translate, max: number) {
  return z
    .string({ required_error: t('validation.required') })
    .trim()
    .min(1, t('validation.required'))
    .max(max, t('validation.maxLength', { max }))
}

export function userNameField(t: Translate) {
  return z
    .string({ required_error: t('validation.required') })
    .trim()
    .min(1, t('validation.required'))
    .regex(USER_NAME_PATTERN, t('validation.userName'))
}

export function newPasswordField(t: Translate) {
  return z
    .string({ required_error: t('validation.required') })
    .min(PASSWORD_MIN_LENGTH, t('validation.passwordLength', { min: PASSWORD_MIN_LENGTH }))
    .max(PASSWORD_MAX_LENGTH, t('validation.maxLength', { max: PASSWORD_MAX_LENGTH }))
}

/** Any non-empty password (sign-in, current password). */
export function passwordField(t: Translate) {
  return z
    .string({ required_error: t('validation.required') })
    .min(1, t('validation.required'))
    .max(PASSWORD_MAX_LENGTH, t('validation.maxLength', { max: PASSWORD_MAX_LENGTH }))
}

/** Parses a decimal typed with a dot or a comma ("0,85"); `null` for an empty string, `NaN` for garbage. */
export function parseDecimal(value: string | number | null | undefined): number | null {
  if (value === null || value === undefined) return null
  if (typeof value === 'number') return value
  const text = value.trim().replace(',', '.')
  if (text === '') return null
  return /^[-+]?(?:\d+\.?\d*|\.\d+)$/.test(text) ? Number(text) : Number.NaN
}

/** An optional decimal text field inside [min, max]. */
export function optionalDecimalField(t: Translate, range: { min?: number; max?: number; message: string }) {
  return z
    .union([z.string(), z.number()])
    .optional()
    .superRefine((value, context) => {
      const number = parseDecimal(value)
      if (number === null) return
      if (Number.isNaN(number)) {
        context.addIssue({ code: z.ZodIssueCode.custom, message: t('validation.number') })
        return
      }
      if ((range.min !== undefined && number < range.min) || (range.max !== undefined && number > range.max))
        context.addIssue({ code: z.ZodIssueCode.custom, message: range.message })
    })
}
