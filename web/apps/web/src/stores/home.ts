import { getHome, type HomeDto } from '@controlhome/api-client'
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { unwrap } from '../lib/problem'

/** The home's settings (`GET /api/home`), shared by the shell and the dashboard. */
export const useHomeStore = defineStore('home', () => {
  const home = ref<HomeDto | null>(null)
  const loading = ref(false)
  const error = ref<unknown>(null)
  let pending: Promise<void> | undefined

  async function load(force = false): Promise<void> {
    if (home.value && !force) return
    pending ??= (async () => {
      loading.value = true
      error.value = null
      try {
        home.value = unwrap(await getHome())
      } catch (failure) {
        error.value = failure
      } finally {
        loading.value = false
        pending = undefined
      }
    })()
    await pending
  }

  function reset(): void {
    home.value = null
    error.value = null
  }

  return { home, loading, error, load, reset }
})
