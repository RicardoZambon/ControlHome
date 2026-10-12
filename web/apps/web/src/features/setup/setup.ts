import type { CompleteSetupRequest, DatabaseSetupRequest } from '@controlhome/api-client'
import type { AppLocale } from '../../i18n'
import { DEFAULT_CURRENCY, DEFAULT_TIME_ZONE } from '../../lib/locale-data'
import { parseDecimal } from '../../lib/validation'

export type SetupStepId = 'code' | 'database' | 'admin' | 'home' | 'review'

/** Every step in wizard order; the database step is only shown while the server has no database connection. */
export const SETUP_STEPS: readonly SetupStepId[] = ['code', 'database', 'admin', 'home', 'review']

/** The wizard's steps: Code → Database (only when `includeDatabase`) → Admin → Home → Review. */
export function setupSteps(includeDatabase: boolean): SetupStepId[] {
  return SETUP_STEPS.filter((step) => includeDatabase || step !== 'database')
}

export function nextStep(steps: readonly SetupStepId[], current: SetupStepId): SetupStepId | undefined {
  const index = steps.indexOf(current)
  return index < 0 ? undefined : steps[index + 1]
}

export function previousStep(steps: readonly SetupStepId[], current: SetupStepId): SetupStepId | undefined {
  const index = steps.indexOf(current)
  return index <= 0 ? undefined : steps[index - 1]
}

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

/** The SQL Server connection as typed (the port stays text until submit). */
export interface DatabaseDraft {
  host: string
  port: string
  database: string
  userName: string
  password: string
  trustServerCertificate: boolean
}

export interface SetupDraft {
  setupCode: string
  database: DatabaseDraft
  admin: AdminDraft
  home: HomeDraft
}

export const DEFAULT_DATABASE_PORT = 1433
export const DEFAULT_DATABASE_NAME = 'ControlHome'

// Mirrors DatabaseSetupRequestValidator (ControlHome.Application/Setup/SetupContracts.cs).
export const DATABASE_HOST_MAX_LENGTH = 253
export const DATABASE_HOST_PATTERN = /^[A-Za-z0-9._:\-[\]]+$/
export const DATABASE_NAME_PATTERN = /^[A-Za-z][A-Za-z0-9_]{0,127}$/
export const SQL_NAME_MAX_LENGTH = 128

/**
 * The database fields' starting values: the host this page was opened from (SQL Server often runs on the same box),
 * port 1433, database `ControlHome`, and trusting the server's own certificate (self-signed on most home servers).
 */
export function createDatabaseDraft(hostname: string): DatabaseDraft {
  return {
    host: hostname.replace(/^\[(.*)\]$/, '$1'),
    port: String(DEFAULT_DATABASE_PORT),
    database: DEFAULT_DATABASE_NAME,
    userName: '',
    password: '',
    trustServerCertificate: true,
  }
}

export function createDraft(locale: AppLocale, hostname = ''): SetupDraft {
  return {
    setupCode: '',
    database: createDatabaseDraft(hostname),
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

/** `POST /api/setup/database`: the setup code entered on the first step plus the database fields. */
export function toDatabaseRequest(setupCode: string, database: DatabaseDraft): DatabaseSetupRequest {
  return {
    setupCode: normalizeSetupCode(setupCode),
    host: database.host.trim(),
    port: Number(database.port.trim()),
    database: database.database.trim(),
    userName: database.userName.trim(),
    password: database.password,
    trustServerCertificate: database.trustServerCertificate,
  }
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
 * Splits the server's field errors of `POST /api/setup/complete` (`setupCode`, `admin.userName`, `home.timeZoneId`, ...)
 * by wizard step, keyed by the step's form field name.
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

const DATABASE_FIELDS: readonly string[] = ['host', 'port', 'database', 'userName', 'password']

/** The same for `POST /api/setup/database` (flat paths: `setupCode`, `host`, `port`, ...). */
export function databaseErrorsByStep(paths: readonly string[]): Partial<Record<SetupStepId, string[]>> {
  const result: Partial<Record<SetupStepId, string[]>> = {}
  for (const path of paths) {
    if (path === 'setupCode') (result.code ??= []).push(path)
    else if (DATABASE_FIELDS.includes(path)) (result.database ??= []).push(path)
  }
  return result
}

/** The first step (in wizard order) that has errors. */
export function firstStepWithErrors(
  errors: Partial<Record<SetupStepId, string[]>>,
  steps: readonly SetupStepId[] = SETUP_STEPS,
): SetupStepId | undefined {
  return steps.find((step) => (errors[step]?.length ?? 0) > 0)
}

export type DatabaseProgress = 'connecting' | 'preparing' | 'ready' | 'failed'

/** Where the database initialization is, from `SetupStatusDto.databaseState`. */
export function databaseProgress(state: string): DatabaseProgress {
  switch (state) {
    case 'Ready':
      return 'ready'
    case 'Failed':
      return 'failed'
    case 'Migrating':
      return 'preparing'
    default:
      return 'connecting'
  }
}
