import type { CompleteSetupRequest } from '@controlhome/api-client'
import type { AppLocale } from '../../i18n'
import { DEFAULT_CURRENCY, DEFAULT_TIME_ZONE } from '../../lib/locale-data'
import { parseDecimal } from '../../lib/validation'

export type SetupStepId = 'code' | 'admin' | 'home' | 'review'
export const SETUP_STEPS: readonly SetupStepId[] = ['code', 'admin', 'home', 'review']

export interface AdminDraft {
  displayName: string
  userName: string
  password: string
  confirmPassword: string
}

/** Home fields as typed (numbers stay text until submit, so "0,85" works). */
export interface HomeDraft {
  name: string
  timeZoneId: string
  latitude: string
  longitude: string
  locale: AppLocale
  currency: string
  pricePerKwh: string
}

export interface SetupDraft {
  setupCode: string
  admin: AdminDraft
  home: HomeDraft
}

export function createDraft(locale: AppLocale): SetupDraft {
  return {
    setupCode: '',
    admin: { displayName: '', userName: '', password: '', confirmPassword: '' },
    home: {
      name: '',
      timeZoneId: DEFAULT_TIME_ZONE,
      latitude: '',
      longitude: '',
      locale,
      currency: DEFAULT_CURRENCY,
      pricePerKwh: '',
    },
  }
}

/** `XXXX-XXXX-XXXX` as typed: upper case, spaces removed (the server ignores dashes and spaces). */
export function normalizeSetupCode(code: string): string {
  return code.toUpperCase().replace(/\s+/g, '')
}

export function toRequest(draft: SetupDraft): CompleteSetupRequest {
  const number = (value: string) => {
    const parsed = parseDecimal(value)
    return parsed === null || Number.isNaN(parsed) ? null : parsed
  }
  return {
    setupCode: normalizeSetupCode(draft.setupCode),
    admin: {
      userName: draft.admin.userName.trim(),
      displayName: draft.admin.displayName.trim(),
      password: draft.admin.password,
    },
    home: {
      name: draft.home.name.trim(),
      timeZoneId: draft.home.timeZoneId,
      latitude: number(draft.home.latitude),
      longitude: number(draft.home.longitude),
      locale: draft.home.locale,
      currency: draft.home.currency,
      pricePerKwh: number(draft.home.pricePerKwh),
    },
  }
}

/**
 * Splits the server's field errors (`setupCode`, `admin.userName`, `home.timeZoneId`, ...) by wizard step, keyed by
 * the step's form field name.
 */
export function errorsByStep(paths: readonly string[]): Partial<Record<SetupStepId, string[]>> {
  const result: Partial<Record<SetupStepId, string[]>> = {}
  const add = (step: SetupStepId, field: string) => (result[step] ??= []).push(field)
  for (const path of paths) {
    if (path === 'setupCode') add('code', 'setupCode')
    else if (path.startsWith('admin.')) add('admin', path.slice('admin.'.length))
    else if (path.startsWith('home.')) add('home', path.slice('home.'.length))
  }
  return result
}

/** The first step (in wizard order) that has errors. */
export function firstStepWithErrors(errors: Partial<Record<SetupStepId, string[]>>): SetupStepId | undefined {
  return SETUP_STEPS.find((step) => (errors[step]?.length ?? 0) > 0)
}
