import { describe, expect, it } from 'vitest'
import { cn } from './utils'

describe('cn', () => {
  it('lets the last conflicting Tailwind class win', () => {
    expect(cn('px-2', 'px-4')).toBe('px-4')
  })

  it('drops falsy values and keeps conditional classes', () => {
    expect(cn('text-sm', false, undefined, null, { 'font-medium': true, hidden: false })).toBe(
      'text-sm font-medium',
    )
  })

  it('merges conflicting semantic color classes', () => {
    expect(cn('bg-primary text-primary-foreground', 'bg-secondary')).toBe(
      'text-primary-foreground bg-secondary',
    )
  })
})
