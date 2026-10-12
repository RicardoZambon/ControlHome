import { describe, expect, it } from 'vitest'
import { useCommandStack } from './useCommandStack'

interface State {
  value: number
}

const s = (value: number): State => ({ value })

describe('useCommandStack', () => {
  it('executes, undoes and redoes', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'one')
    stack.execute(s(2), 'two')
    expect(stack.present.value.value).toBe(2)
    expect(stack.canUndo.value).toBe(true)
    expect(stack.undoLabel.value).toBe('two')

    stack.undo()
    expect(stack.present.value.value).toBe(1)
    expect(stack.redoLabel.value).toBe('two')
    stack.undo()
    expect(stack.present.value.value).toBe(0)
    expect(stack.canUndo.value).toBe(false)
    stack.undo() // nothing left: no-op
    expect(stack.present.value.value).toBe(0)

    stack.redo()
    stack.redo()
    expect(stack.present.value.value).toBe(2)
    expect(stack.canRedo.value).toBe(false)
    stack.redo() // nothing left: no-op
    expect(stack.present.value.value).toBe(2)
  })

  it('a new command after an undo drops the redo history', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'one')
    stack.execute(s(2), 'two')
    stack.undo()
    stack.execute(s(3), 'three')
    expect(stack.canRedo.value).toBe(false)
    stack.undo()
    expect(stack.present.value.value).toBe(1)
  })

  it('ignores a command that does not change the state', () => {
    const stack = useCommandStack(s(0))
    stack.execute(stack.present.value, 'nothing')
    expect(stack.canUndo.value).toBe(false)
  })

  it('merges consecutive commands with the same merge key (typing a name is one step)', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'rename', 'name:a')
    stack.execute(s(2), 'rename', 'name:a')
    stack.execute(s(3), 'rename', 'name:a')
    stack.execute(s(4), 'rename', 'name:b')
    stack.undo()
    expect(stack.present.value.value).toBe(3)
    stack.undo()
    expect(stack.present.value.value).toBe(0)
  })

  it('does not merge across an undo', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'rename', 'name:a')
    stack.undo()
    stack.execute(s(2), 'rename', 'name:a')
    stack.execute(s(3), 'rename', 'name:a')
    stack.undo()
    expect(stack.present.value.value).toBe(0)
  })

  it('a transaction (a drag) previews every move and records one step', () => {
    const stack = useCommandStack(s(0))
    stack.begin()
    stack.update(s(1))
    stack.update(s(2))
    expect(stack.present.value.value).toBe(2)
    expect(stack.canUndo.value).toBe(false)
    stack.commit('drag')
    expect(stack.undoLabel.value).toBe('drag')
    stack.undo()
    expect(stack.present.value.value).toBe(0)
  })

  it('a transaction that changes nothing records nothing; rollback restores the start', () => {
    const stack = useCommandStack(s(0))
    stack.begin()
    stack.commit('click')
    expect(stack.canUndo.value).toBe(false)

    stack.begin()
    stack.update(s(5))
    stack.rollback()
    expect(stack.present.value.value).toBe(0)
    expect(stack.canUndo.value).toBe(false)
  })

  it('undo during a transaction only cancels the transaction', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'one')
    stack.begin()
    stack.update(s(9))
    stack.undo()
    expect(stack.present.value.value).toBe(1)
    stack.undo()
    expect(stack.present.value.value).toBe(0)
  })

  it('keeps at most `limit` steps', () => {
    const stack = useCommandStack(s(0), { limit: 3 })
    for (let i = 1; i <= 5; i++) stack.execute(s(i), `step ${i}`)
    stack.undo()
    stack.undo()
    stack.undo()
    expect(stack.canUndo.value).toBe(false)
    expect(stack.present.value.value).toBe(2)
  })

  it('tracks unsaved changes against the saved state', () => {
    const stack = useCommandStack(s(0))
    expect(stack.isDirty.value).toBe(false)
    stack.execute(s(1), 'one')
    expect(stack.isDirty.value).toBe(true)
    stack.undo()
    expect(stack.isDirty.value).toBe(false)
    stack.redo()
    stack.markSaved()
    expect(stack.isDirty.value).toBe(false)
    stack.undo()
    expect(stack.isDirty.value).toBe(true)
  })

  it('reset replaces the state and clears the history', () => {
    const stack = useCommandStack(s(0))
    stack.execute(s(1), 'one')
    stack.reset(s(7))
    expect(stack.present.value.value).toBe(7)
    expect(stack.canUndo.value).toBe(false)
    expect(stack.canRedo.value).toBe(false)
    expect(stack.isDirty.value).toBe(false)
  })
})
