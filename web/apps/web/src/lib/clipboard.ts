/**
 * Copies `text` to the clipboard. The Clipboard API exists only in secure contexts (HTTPS or localhost), and ControlHome
 * is usually opened over plain HTTP on the home network, so a selected off-screen text area is copied there instead.
 * Returns whether the copy worked.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  if (typeof navigator !== 'undefined' && navigator.clipboard && globalThis.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // Denied: fall back below.
    }
  }
  if (typeof document === 'undefined') return false
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '0'
  area.style.left = '-9999px'
  document.body.append(area)
  try {
    area.select()
    // Deprecated, but the only way to copy outside a secure context.
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    area.remove()
  }
}
