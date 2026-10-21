/**
 * Backup export and import (docs/architecture.md, milestone 1.3-C): the passphrase rules of `ExportBackupRequest`
 * (12–256 characters, only when the integrations' secrets go in) and the file name of a download.
 */

export const PASSPHRASE_MIN = 12
export const PASSPHRASE_MAX = 256

export type PassphraseProblem = 'required' | 'tooShort' | 'tooLong' | 'mismatch'

/** Checks a new passphrase and its confirmation; `null` when it is fine. */
export function passphraseProblem(passphrase: string, confirmation: string): PassphraseProblem | null {
  if (!passphrase) return 'required'
  if (passphrase.length < PASSPHRASE_MIN) return 'tooShort'
  if (passphrase.length > PASSPHRASE_MAX) return 'tooLong'
  return passphrase === confirmation ? null : 'mismatch'
}

/**
 * The file name the server gave the download (`Content-Disposition`, RFC 6266 `filename*` first), else
 * `controlhome-backup-<date>.zip`. Path separators and control characters never get through.
 */
export function backupFileName(contentDisposition: string | null, now: Date): string {
  const fallback = `controlhome-backup-${now.toISOString().slice(0, 10)}.zip`
  if (!contentDisposition) return fallback
  const extended = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(contentDisposition)
  const plain = /filename\s*=\s*"?([^";]+)"?/.exec(contentDisposition)
  let name: string | undefined
  try {
    name = extended ? decodeURIComponent(extended[1]!.trim()) : plain?.[1]?.trim()
  } catch {
    name = plain?.[1]?.trim()
  }
  const safe = name?.replace(/[\\/\u0000-\u001f]/g, '').trim()
  return safe ? safe : fallback
}

/** Whether a chosen file looks like a backup (a zip), before sending it. */
export function looksLikeBackup(file: Pick<File, 'name' | 'type'>): boolean {
  return file.name.toLowerCase().endsWith('.zip') || file.type === 'application/zip'
}

/** Saves a blob as a file (a temporary object URL and a download link). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.rel = 'noopener'
  document.body.append(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1_000)
}
