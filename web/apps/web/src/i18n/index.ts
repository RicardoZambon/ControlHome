import { createI18n } from 'vue-i18n'
import en from '../locales/en.json'
import ptBR from '../locales/pt-BR.json'

/** Languages of the UI (and of `MeDto.locale` / `HomeDto.locale`). */
export const SUPPORTED_LOCALES = ['en', 'pt-BR'] as const
export type AppLocale = (typeof SUPPORTED_LOCALES)[number]
export const DEFAULT_LOCALE: AppLocale = 'en'
/** localStorage key of the UI language (mirrored by public/theme-init.js). */
export const LOCALE_STORAGE_KEY = 'ch.locale'

export interface MessageTree {
  [key: string]: string | MessageTree
}

export function isAppLocale(value: unknown): value is AppLocale {
  return typeof value === 'string' && (SUPPORTED_LOCALES as readonly string[]).includes(value)
}

// Each feature ships `features/<feature>/locales/<locale>.json`; its keys live under the `<feature>` namespace.
const featureLocaleFiles = import.meta.glob<MessageTree>('../features/*/locales/*.json', {
  eager: true,
  import: 'default',
})

/** The global messages (`src/locales`) plus every feature's messages under its folder name. */
export function buildMessages(
  features: Record<string, MessageTree> = featureLocaleFiles,
): Record<AppLocale, MessageTree> {
  const messages: Record<AppLocale, MessageTree> = { en: { ...en }, 'pt-BR': { ...ptBR } }
  for (const [path, tree] of Object.entries(features)) {
    const match = /\/features\/([^/]+)\/locales\/([^/]+)\.json$/.exec(path)
    if (!match) continue
    const [, feature, locale] = match as unknown as [string, string, string]
    if (!isAppLocale(locale)) throw new Error(`Unsupported locale file: ${path}`)
    if (feature in messages[locale])
      throw new Error(`The i18n namespace "${feature}" is defined twice (${path}).`)
    messages[locale][feature] = tree
  }
  return messages
}

function readStoredLocale(): string | null {
  try {
    return globalThis.localStorage?.getItem(LOCALE_STORAGE_KEY) ?? null
  } catch {
    return null
  }
}

/** The language before sign-in: the saved choice, else Portuguese for a `pt*` browser, else English. */
export function detectLocale(): AppLocale {
  const stored = readStoredLocale()
  if (isAppLocale(stored)) return stored
  const language = typeof navigator === 'undefined' ? '' : (navigator.language ?? '')
  return language.toLowerCase().startsWith('pt') ? 'pt-BR' : 'en'
}

const isTest = import.meta.env.MODE === 'test'

/** Prefix of the console warning for a missing key (the Playwright gates in web/tests/e2e fail on it). */
const MISSING_KEY_WARNING = '[i18n] Missing key'

function createI18nInstance(locale: AppLocale) {
  return createI18n({
    legacy: false,
    locale,
    fallbackLocale: DEFAULT_LOCALE,
    messages: buildMessages(),
    missingWarn: !isTest,
    fallbackWarn: false,
    // Tests fail on any key that is missing in the active locale (every key must exist in en and pt-BR). Elsewhere a
    // missing key is a console warning, in production builds too (vue-i18n's own warning exists only in development),
    // and the text falls back to English.
    missing: isTest
      ? (missingLocale: string, key: string) => {
          throw new Error(`Missing i18n key "${key}" for locale "${missingLocale}".`)
        }
      : (missingLocale: string, key: string) => {
          console.warn(`${MISSING_KEY_WARNING} "${key}" for locale "${missingLocale}".`)
        },
  })
}

export type AppI18n = ReturnType<typeof createI18nInstance>

let active: AppI18n | undefined

function applyDocumentLocale(locale: AppLocale): void {
  if (typeof document !== 'undefined') document.documentElement.lang = locale
}

/** Creates the app's i18n instance (Composition API) in the detected language and sets `<html lang>`. */
export function createAppI18n(options: { locale?: AppLocale } = {}): AppI18n {
  const locale = options.locale ?? detectLocale()
  active = createI18nInstance(locale)
  applyDocumentLocale(locale)
  return active
}

/** Switches the UI language: active i18n instance, `localStorage['ch.locale']` and `<html lang>`. */
export function setLocale(locale: AppLocale): void {
  if (active) active.global.locale.value = locale
  try {
    globalThis.localStorage?.setItem(LOCALE_STORAGE_KEY, locale)
  } catch {
    // Not persisted; the language still applies to this page.
  }
  applyDocumentLocale(locale)
}

/** The language in effect. */
export function currentLocale(): AppLocale {
  const locale = active?.global.locale.value
  return isAppLocale(locale) ? locale : detectLocale()
}

/** The active instance, for code outside components (router hooks, API error handlers). */
export function appI18n(): AppI18n {
  if (!active) throw new Error('createAppI18n() has not been called.')
  return active
}
