import { existsSync, readFileSync, readdirSync } from 'node:fs'

// The app's own messages (apps/web/src/locales + every feature's locales, namespaced by folder like src/i18n does), so
// specs find elements by the text users see in either language instead of copies of it.

export type Locale = 'en' | 'pt-BR'
export const LOCALES: readonly Locale[] = ['en', 'pt-BR']

interface MessageTree {
  [key: string]: string | MessageTree
}

const appSource = new URL('../../apps/web/src/', import.meta.url)

function readJson(url: URL): MessageTree {
  return JSON.parse(readFileSync(url, 'utf8')) as MessageTree
}

function loadMessages(locale: Locale): MessageTree {
  const messages = readJson(new URL(`locales/${locale}.json`, appSource))
  for (const feature of readdirSync(new URL('features/', appSource))) {
    const file = new URL(`features/${feature}/locales/${locale}.json`, appSource)
    if (existsSync(file)) messages[feature] = readJson(file)
  }
  return messages
}

const messages: Record<Locale, MessageTree> = { en: loadMessages('en'), 'pt-BR': loadMessages('pt-BR') }

/** The message `key` in `locale` with `{name}` placeholders filled in (throws for an unknown key). */
export function tr(locale: Locale, key: string, params: Record<string, string | number> = {}): string {
  let node: string | MessageTree | undefined = messages[locale]
  for (const part of key.split('.')) node = typeof node === 'object' ? node[part] : undefined
  if (typeof node !== 'string') throw new Error(`No message "${key}" for locale "${locale}".`)
  return node.replace(/\{(\w+)\}/g, (placeholder, name: string) =>
    name in params ? String(params[name]) : placeholder,
  )
}

/** English message, for the specs that run in English. */
export const t = (key: string, params?: Record<string, string | number>): string => tr('en', key, params)
