import { describe, expect, it } from 'vitest'
import { backupFileName, formatBytes, looksLikeBackup, passphraseProblem } from './backup'

const NOW = new Date('2026-10-21T12:00:00Z')

describe('passphraseProblem', () => {
  it('needs 12 to 256 characters, typed twice', () => {
    expect(passphraseProblem('', '')).toBe('required')
    expect(passphraseProblem('short', 'short')).toBe('tooShort')
    expect(passphraseProblem('x'.repeat(257), 'x'.repeat(257))).toBe('tooLong')
    expect(passphraseProblem('correct horse battery', 'correct horse batery')).toBe('mismatch')
    expect(passphraseProblem('correct horse battery', 'correct horse battery')).toBeNull()
  })
})

describe('backupFileName', () => {
  it('takes the server’s name, the RFC 6266 one first', () => {
    expect(backupFileName('attachment; filename=controlhome-2026-10-21.zip', NOW)).toBe(
      'controlhome-2026-10-21.zip',
    )
    expect(backupFileName(`attachment; filename="plain.zip"; filename*=UTF-8''casa%20azul.zip`, NOW)).toBe(
      'casa azul.zip',
    )
  })

  it('falls back to a dated name and never lets a path through', () => {
    expect(backupFileName(null, NOW)).toBe('controlhome-backup-2026-10-21.zip')
    expect(backupFileName('attachment; filename="../../etc/x.zip"', NOW)).toBe('....etcx.zip')
    expect(backupFileName('attachment', NOW)).toBe('controlhome-backup-2026-10-21.zip')
  })
})

describe('formatBytes', () => {
  it('says kB under a megabyte and MB above, in the user language', () => {
    expect(formatBytes(820_400, 'en')).toBe('820 kB')
    expect(formatBytes(1_430_000, 'en')).toBe('1.4 MB')
    expect(formatBytes(1_430_000, 'pt-BR')).toBe('1,4 MB')
    expect(formatBytes(12, 'en')).toBe('1 kB')
  })
})

describe('looksLikeBackup', () => {
  it('accepts zip files', () => {
    expect(looksLikeBackup({ name: 'Backup.ZIP', type: '' })).toBe(true)
    expect(looksLikeBackup({ name: 'backup', type: 'application/zip' })).toBe(true)
    expect(looksLikeBackup({ name: 'plan.png', type: 'image/png' })).toBe(false)
  })
})
