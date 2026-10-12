import {
  completeSetup as apiCompleteSetup,
  getMe,
  getSetupStatus,
  login as apiLogin,
  logout as apiLogout,
  updateProfile as apiUpdateProfile,
  type CompleteSetupRequest,
  type LoginRequest,
  type MeDto,
  type SetupStatusDto,
  type UpdateProfileRequest,
} from '@controlhome/api-client'
import { useTheme, type ThemeMode } from '@controlhome/ui'
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { isAppLocale, setLocale, type AppLocale } from '../i18n'
import { applyPreferences, isThemeMode } from '../lib/preferences'
import { unwrap } from '../lib/problem'

export type Role = 'Admin' | 'Member' | 'Viewer'

/** Who is signed in and the first-run state of the server. */
export const useSessionStore = defineStore('session', () => {
  const me = ref<MeDto | null>(null)
  const status = ref<SetupStatusDto | null>(null)
  /** Whether `/api/auth/me` was asked (so an anonymous visitor is not probed on every navigation). */
  const meLoaded = ref(false)

  const isAuthenticated = computed(() => me.value !== null)
  const isAdmin = computed(() => hasRole('Admin'))

  function hasRole(...roles: string[]): boolean {
    const current = me.value
    return current !== null && roles.some((role) => current.roles.includes(role))
  }

  function setMe(value: MeDto | null, apply = true): void {
    me.value = value
    meLoaded.value = true
    if (value && apply) applyPreferences(value)
  }

  /** `GET /api/setup/status`; keeps the previous value when the server can't be reached. */
  async function loadStatus(): Promise<SetupStatusDto | null> {
    const { data } = await getSetupStatus()
    if (data) status.value = data
    return status.value
  }

  async function ensureStatus(): Promise<void> {
    if (!status.value) await loadStatus()
  }

  /** Restores the session from the cookie (`GET /api/auth/me`) and applies the user's language and theme. */
  async function loadMe(): Promise<MeDto | null> {
    const { data } = await getMe()
    setMe(data ?? null)
    return me.value
  }

  async function ensureMe(): Promise<void> {
    if (!meLoaded.value) await loadMe()
  }

  /** Signs in and applies the user's saved language and theme. Throws `ApiError`. */
  async function login(request: LoginRequest): Promise<MeDto> {
    const data = unwrap(await apiLogin({ body: request }))
    setMe(data)
    return data
  }

  /** Ends the session; the local state is cleared even when the request fails. */
  async function logout(): Promise<void> {
    try {
      await apiLogout()
    } finally {
      setMe(null)
    }
  }

  /** Forgets the user locally (the session already ended on the server). */
  function clear(): void {
    setMe(null)
  }

  /**
   * Completes the first-run setup (the admin is signed in). The new account starts with the home's language and the
   * `system` theme; a theme chosen in this browser during setup is saved to the profile so it survives the next login.
   */
  async function completeSetup(request: CompleteSetupRequest): Promise<MeDto> {
    const data = unwrap(await apiCompleteSetup({ body: request }))
    if (status.value) status.value = { ...status.value, setupCompleted: true }
    setMe(data, false)
    if (isAppLocale(data.locale)) setLocale(data.locale)
    const localMode = useTheme().mode.value
    if (localMode !== data.theme) {
      try {
        await savePreferences({ theme: localMode })
      } catch {
        // Best effort: the theme still applies in this browser.
      }
    }
    return me.value ?? data
  }

  /** `PUT /api/auth/me/profile`, then applies the saved language and theme. Throws `ApiError`. */
  async function updateProfile(request: UpdateProfileRequest): Promise<MeDto> {
    const data = unwrap(await apiUpdateProfile({ body: request }))
    setMe(data)
    return data
  }

  /** Saves a language or theme picked from the top bar to the signed-in user's profile. */
  async function savePreferences(preferences: { locale?: AppLocale; theme?: ThemeMode }): Promise<void> {
    const current = me.value
    if (!current) return
    const theme = preferences.theme ?? (isThemeMode(current.theme) ? current.theme : 'system')
    await updateProfile({
      displayName: current.displayName,
      locale: preferences.locale ?? current.locale,
      theme,
    })
  }

  return {
    me,
    status,
    meLoaded,
    isAuthenticated,
    isAdmin,
    hasRole,
    loadStatus,
    ensureStatus,
    loadMe,
    ensureMe,
    login,
    logout,
    clear,
    completeSetup,
    updateProfile,
    savePreferences,
  }
})
