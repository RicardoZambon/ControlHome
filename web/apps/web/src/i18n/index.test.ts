import { afterEach, describe, expect, it, vi } from 'vitest'
// The API's error codes (the server is the source of every ProblemDetails code).
import errorCodesSource from '../../../../../src/ControlHome.Application/Common/ErrorCodes.cs?raw'
import {
  LOCALE_STORAGE_KEY,
  SUPPORTED_LOCALES,
  buildMessages,
  createAppI18n,
  detectLocale,
  setLocale,
  type MessageTree,
} from './index'

function flatten(tree: MessageTree, prefix = ''): Map<string, string> {
  const entries = new Map<string, string>()
  for (const [key, value] of Object.entries(tree)) {
    const path = prefix ? `${prefix}.${key}` : key
    if (typeof value === 'string') entries.set(path, value)
    else for (const [nested, text] of flatten(value, path)) entries.set(nested, text)
  }
  return entries
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('createAppI18n', () => {
  it('throws on a missing key in tests', () => {
    const i18n = createAppI18n({ locale: 'en' })
    expect(() => i18n.global.t('nope.not.there')).toThrow(/Missing i18n key "nope.not.there"/)
  })

  it('throws when a key exists in English only (no silent fallback)', () => {
    const i18n = createAppI18n({ locale: 'pt-BR' })
    i18n.global.mergeLocaleMessage('en', { onlyEnglish: 'Only here' })
    expect(() => i18n.global.t('onlyEnglish')).toThrow(/Missing i18n key "onlyEnglish"/)
  })

  it('throws on a message with broken syntax (so the "compiles" test below means something)', () => {
    const i18n = createAppI18n({ locale: 'en' })
    i18n.global.mergeLocaleMessage('en', { broken: 'Hello {name' })
    expect(() => i18n.global.t('broken', { name: 'x' })).toThrow()
  })

  it('sets <html lang>', () => {
    createAppI18n({ locale: 'pt-BR' })
    expect(document.documentElement.lang).toBe('pt-BR')
  })
})

describe('messages', () => {
  const messages = buildMessages()
  const en = flatten(messages.en)
  const ptBR = flatten(messages['pt-BR'])

  it('has the same keys in en and pt-BR', () => {
    expect([...ptBR.keys()].sort()).toEqual([...en.keys()].sort())
  })

  it('has no empty messages', () => {
    for (const [key, text] of [...en, ...ptBR]) expect(text.trim(), key).not.toBe('')
  })

  it.each(SUPPORTED_LOCALES)('compiles and renders every %s message', (locale) => {
    const i18n = createAppI18n({ locale })
    for (const key of en.keys()) {
      expect(() => i18n.global.t(key, {}), key).not.toThrow()
    }
  })

  it('namespaces feature messages by folder', () => {
    expect(en.get('users.title')).toBeTruthy()
    expect(en.get('setup.steps.code.title')).toBeTruthy()
  })

  it('rejects a feature namespace defined twice', () => {
    expect(() =>
      buildMessages({
        '../features/app/locales/en.json': { title: 'x' },
      }),
    ).toThrow(/defined twice/)
  })

  // Every API error code (ControlHome.Application/Common/ErrorCodes.cs) has a translated message.
  it('translates every API error code in both languages', () => {
    const codes = [...errorCodesSource.matchAll(/const string \w+ = "([^"]+)";/g)].map((match) => match[1]!)
    expect(codes.length).toBeGreaterThan(20)
    for (const code of codes) {
      expect(en.has(`errors.${code}`), `en errors.${code}`).toBe(true)
      expect(ptBR.has(`errors.${code}`), `pt-BR errors.${code}`).toBe(true)
    }
  })
})

describe('detectLocale', () => {
  it('prefers the saved language', () => {
    localStorage.setItem(LOCALE_STORAGE_KEY, 'pt-BR')
    vi.stubGlobal('navigator', { language: 'en-US' })
    expect(detectLocale()).toBe('pt-BR')
  })

  it.each([
    ['pt-BR', 'pt-BR'],
    ['pt-PT', 'pt-BR'],
    ['pt', 'pt-BR'],
    ['en-US', 'en'],
    ['de-DE', 'en'],
    ['', 'en'],
  ])('maps the browser language %s to %s', (language, expected) => {
    vi.stubGlobal('navigator', { language })
    expect(detectLocale()).toBe(expected)
  })

  it('ignores an unsupported saved value', () => {
    localStorage.setItem(LOCALE_STORAGE_KEY, 'fr')
    vi.stubGlobal('navigator', { language: 'pt-BR' })
    expect(detectLocale()).toBe('pt-BR')
  })
})

describe('setLocale', () => {
  it('switches the language, saves it and sets <html lang>', () => {
    const i18n = createAppI18n({ locale: 'en' })
    setLocale('pt-BR')
    expect(i18n.global.locale.value).toBe('pt-BR')
    expect(localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('pt-BR')
    expect(document.documentElement.lang).toBe('pt-BR')
  })
})
