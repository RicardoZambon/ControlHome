// Runs before the first paint (a classic script in <head>; the CSP allows only same-origin scripts, so no inline code).
// Applies the saved theme so the page never flashes the wrong one, and the language for screen readers.
// Mirrors useTheme() in @controlhome/ui (localStorage 'ch.theme': light | dark | system, default system) and
// detectLocale() in src/i18n (localStorage 'ch.locale', else pt-BR when the browser language starts with 'pt', else en).
;(function () {
  var root = document.documentElement

  function read(key) {
    try {
      return window.localStorage.getItem(key)
    } catch {
      return null
    }
  }

  var mode = read('ch.theme')
  if (mode !== 'light' && mode !== 'dark' && mode !== 'system') mode = 'system'
  var prefersDark = false
  try {
    prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches
  } catch {
    prefersDark = false
  }
  root.classList.toggle('dark', mode === 'dark' || (mode === 'system' && prefersDark))

  var locale = read('ch.locale')
  if (locale !== 'en' && locale !== 'pt-BR') {
    var language = (navigator.language || '').toLowerCase()
    locale = language.indexOf('pt') === 0 ? 'pt-BR' : 'en'
  }
  root.lang = locale
})()
