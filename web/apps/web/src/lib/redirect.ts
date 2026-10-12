/**
 * A `?redirect=` value that is safe to navigate to after sign-in: an app-relative path. Anything that could leave the
 * app (`https://…`, `//host`, `/\host`, `javascript:`) is dropped.
 */
export function safeRedirect(value: unknown): string | undefined {
  const candidate = Array.isArray(value) ? value[0] : value
  if (typeof candidate !== 'string') return undefined
  if (!candidate.startsWith('/') || candidate.startsWith('//') || candidate.startsWith('/\\'))
    return undefined
  // Control characters (tab, newline, ...) are never part of a real app path; browsers strip some of them.
  for (const char of candidate) if (char.charCodeAt(0) < 0x20 || char === '\u007f') return undefined
  return candidate
}
