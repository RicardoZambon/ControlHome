import { computed, shallowRef } from 'vue'

interface Entry<T> {
  /** The state on the other side of the step (before it in `past`, after it in `future`). */
  state: T
  /** What the step did (for the Undo / Redo tooltips). */
  label: string
}

export interface CommandStackOptions {
  /** How many steps can be undone (oldest dropped first). */
  limit?: number
}

/**
 * Undo/redo over immutable snapshots of an editable draft. Every command replaces the whole state with a new object
 * (structural sharing keeps that cheap), so undo is restoring the previous snapshot and "unsaved changes" is an
 * identity check against the last saved one.
 *
 * - `execute(next, label, mergeKey?)`: one step; consecutive steps with the same `mergeKey` (typing a name) merge.
 * - `begin()` / `update(next)` / `commit(label)` / `rollback()`: a transaction, e.g. a drag that previews every pointer
 *   move but records one step.
 */
export function useCommandStack<T>(initial: T, options: CommandStackOptions = {}) {
  const limit = options.limit ?? 200
  const present = shallowRef<T>(initial)
  const past = shallowRef<Entry<T>[]>([])
  const future = shallowRef<Entry<T>[]>([])
  const saved = shallowRef<T>(initial)
  let lastMergeKey: string | undefined
  let transactionBase: T | null = null

  function push(entry: Entry<T>): void {
    const next = [...past.value, entry]
    past.value = next.length > limit ? next.slice(next.length - limit) : next
    future.value = []
  }

  function execute(next: T, label: string, mergeKey?: string): void {
    if (transactionBase !== null) rollback()
    if (Object.is(next, present.value)) return
    const merges =
      mergeKey !== undefined &&
      mergeKey === lastMergeKey &&
      past.value.length > 0 &&
      future.value.length === 0
    if (merges) {
      const entries = [...past.value]
      entries[entries.length - 1] = { ...entries[entries.length - 1]!, label }
      past.value = entries
    } else {
      push({ state: present.value, label })
    }
    present.value = next
    lastMergeKey = mergeKey
  }

  function begin(): void {
    if (transactionBase !== null) rollback()
    transactionBase = present.value
    lastMergeKey = undefined
  }

  function update(next: T): void {
    if (transactionBase === null) transactionBase = present.value
    present.value = next
  }

  function commit(label: string): void {
    const base = transactionBase
    transactionBase = null
    if (base === null || Object.is(base, present.value)) return
    push({ state: base, label })
    lastMergeKey = undefined
  }

  function rollback(): void {
    if (transactionBase === null) return
    present.value = transactionBase
    transactionBase = null
  }

  function undo(): void {
    if (transactionBase !== null) {
      rollback()
      return
    }
    const entry = past.value.at(-1)
    if (!entry) return
    past.value = past.value.slice(0, -1)
    future.value = [...future.value, { state: present.value, label: entry.label }]
    present.value = entry.state
    lastMergeKey = undefined
  }

  function redo(): void {
    if (transactionBase !== null) rollback()
    const entry = future.value.at(-1)
    if (!entry) return
    future.value = future.value.slice(0, -1)
    past.value = [...past.value, { state: present.value, label: entry.label }]
    present.value = entry.state
    lastMergeKey = undefined
  }

  /** `state` (default: the current one) is what the server has now. */
  function markSaved(state: T = present.value): void {
    saved.value = state
  }

  /** Starts over from `state` (loaded or reloaded from the server): no history, nothing unsaved. */
  function reset(state: T): void {
    transactionBase = null
    lastMergeKey = undefined
    past.value = []
    future.value = []
    present.value = state
    saved.value = state
  }

  return {
    present,
    saved,
    execute,
    begin,
    update,
    commit,
    rollback,
    undo,
    redo,
    markSaved,
    reset,
    canUndo: computed(() => past.value.length > 0),
    canRedo: computed(() => future.value.length > 0),
    undoLabel: computed(() => past.value.at(-1)?.label),
    redoLabel: computed(() => future.value.at(-1)?.label),
    isDirty: computed(() => !Object.is(present.value, saved.value)),
  }
}

export type CommandStack<T> = ReturnType<typeof useCommandStack<T>>
