import type { UserDto } from '@controlhome/api-client'

export const ROLES = ['Admin', 'Member', 'Viewer'] as const
export type UserRole = (typeof ROLES)[number]

/** Admins first, then members, then viewers; by name inside each role. */
export function sortUsers(users: readonly UserDto[], locale: string): UserDto[] {
  const rank = (role: string) => {
    const index = (ROLES as readonly string[]).indexOf(role)
    return index === -1 ? ROLES.length : index
  }
  return [...users].sort(
    (a, b) =>
      rank(a.role) - rank(b.role) ||
      a.displayName.localeCompare(b.displayName, locale, { sensitivity: 'base' }) ||
      a.userName.localeCompare(b.userName),
  )
}

/** Replaces the user with the same id, or appends a new one. */
export function upsertUser(users: readonly UserDto[], user: UserDto): UserDto[] {
  const index = users.findIndex((existing) => existing.id === user.id)
  if (index === -1) return [...users, user]
  const copy = [...users]
  copy[index] = user
  return copy
}
