import { getCameraSnapshot, getProblemCode } from '@controlhome/api-client'
import { onBeforeUnmount, onMounted, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'

/** How often a shown snapshot is refreshed. */
export const SNAPSHOT_REFRESH_MS = 10_000

/**
 * A camera's latest still image as an object URL, refreshed every 10 s while the page is visible and `active` is true.
 * Each new picture revokes the URL of the one it replaces; unmounting revokes the last. A failed refresh keeps the last
 * picture and sets `problemCode` (e.g. `cameras.no_snapshot`). Call from a component's setup.
 */
export function useCameraSnapshot(
  endpointId: MaybeRefOrGetter<string>,
  active: MaybeRefOrGetter<boolean> = true,
) {
  const url = ref<string>()
  const problemCode = ref<string>()
  const loading = ref(false)

  let timer: ReturnType<typeof setTimeout> | undefined
  let mounted = false
  /** Bumped when the endpoint changes or the component unmounts: a late answer is dropped. */
  let generation = 0

  function replace(next: string | undefined): void {
    if (url.value) URL.revokeObjectURL(url.value)
    url.value = next
  }

  const running = () =>
    mounted && toValue(active) && (typeof document === 'undefined' || document.visibilityState !== 'hidden')

  function schedule(): void {
    clearTimeout(timer)
    timer = undefined
    if (running()) timer = setTimeout(() => void refresh(), SNAPSHOT_REFRESH_MS)
  }

  /** Fetches a new picture now (and restarts the 10 s timer). */
  async function refresh(): Promise<void> {
    clearTimeout(timer)
    timer = undefined
    const current = generation
    loading.value = true
    try {
      const result = await getCameraSnapshot({ path: { id: toValue(endpointId) }, parseAs: 'blob' })
      if (current !== generation) return
      if (result.error !== undefined) {
        problemCode.value = getProblemCode(result.error) ?? 'unknown'
      } else {
        const blob = result.data as unknown as Blob | undefined
        if (blob && blob.size > 0) {
          problemCode.value = undefined
          replace(URL.createObjectURL(blob))
        }
      }
    } catch {
      if (current === generation) problemCode.value = 'network'
    } finally {
      if (current === generation) {
        loading.value = false
        schedule()
      }
    }
  }

  function onVisibility(): void {
    if (running()) void refresh()
    else schedule()
  }

  onMounted(() => {
    mounted = true
    document.addEventListener('visibilitychange', onVisibility)
    if (running()) void refresh()
  })

  onBeforeUnmount(() => {
    mounted = false
    generation++
    clearTimeout(timer)
    document.removeEventListener('visibilitychange', onVisibility)
    replace(undefined)
  })

  watch(
    () => toValue(endpointId),
    () => {
      generation++
      problemCode.value = undefined
      replace(undefined)
      if (running()) void refresh()
    },
  )
  watch(
    () => toValue(active),
    (now) => {
      if (now && running()) void refresh()
      else schedule()
    },
  )

  return { url, problemCode, loading, refresh }
}
