import { toHexColor, useThemeTokens } from '@controlhome/ui'
import { watchEffect } from 'vue'

/**
 * Draws the browser-tab icon (the BrandMark glyph) from the current theme tokens, so it follows light/dark without a
 * color literal anywhere outside tokens.css. Call once at startup.
 */
export function useThemedFavicon(): void {
  if (typeof document === 'undefined') return
  const tokens = useThemeTokens()
  watchEffect(() => {
    const background = tokens.value['primary']
    const foreground = tokens.value['primary-foreground']
    if (!background || !foreground) return
    const tile = toHexColor(background)
    const glyph = toHexColor(foreground)
    const svg =
      `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">` +
      `<rect width="32" height="32" rx="8" fill="${tile}"/>` +
      `<g fill="none" stroke="${glyph}" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">` +
      `<path d="M7 15.2 16 8l9 7.2V24a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 7 24z"/>` +
      `<path d="M12.6 17.4a4.8 4.8 0 0 1 6.8 0"/></g>` +
      `<circle cx="16" cy="20.4" r="1.7" fill="${glyph}"/></svg>`
    let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    if (!link) {
      link = document.createElement('link')
      link.rel = 'icon'
      document.head.append(link)
    }
    link.type = 'image/svg+xml'
    link.href = `data:image/svg+xml,${encodeURIComponent(svg)}`
  })
}
