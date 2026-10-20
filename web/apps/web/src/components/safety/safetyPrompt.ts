import { defineStore } from 'pinia'
import { shallowRef } from 'vue'

/** What the PIN dialog asks to confirm. */
export interface StepUpPrompt {
  /** The action, e.g. "Unlock · Front door lock". */
  action?: string
}

/**
 * The safety-PIN step-up (docs/architecture.md §8): `requestStepUp` opens the one `SafetyPinDialog` (mounted by the
 * signed-in layout) and resolves `true` once `POST /api/auth/step-up` succeeded, `false` when the person cancels.
 */
export const useSafetyPromptStore = defineStore('safetyPrompt', () => {
  const prompt = shallowRef<StepUpPrompt | null>(null)
  let resolver: ((granted: boolean) => void) | undefined

  function requestStepUp(request: StepUpPrompt = {}): Promise<boolean> {
    // A newer request replaces an open one (that one counts as cancelled).
    resolver?.(false)
    prompt.value = request
    return new Promise<boolean>((resolve) => {
      resolver = resolve
    })
  }

  /** Called by the dialog: the step-up succeeded (`true`) or the dialog was dismissed. */
  function complete(granted: boolean): void {
    const resolve = resolver
    resolver = undefined
    prompt.value = null
    resolve?.(granted)
  }

  return { prompt, requestStepUp, complete }
})
