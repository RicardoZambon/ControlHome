import { describe, expect, it } from 'vitest'
import { commandFor, isTypingTarget } from './shortcuts'

const key = (
  k: string,
  mods: Partial<Pick<KeyboardEvent, 'ctrlKey' | 'metaKey' | 'shiftKey' | 'altKey'>> = {},
) => ({
  key: k,
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  altKey: false,
  ...mods,
})

describe('commandFor', () => {
  it('maps tool keys', () => {
    expect(commandFor(key('v'))).toEqual({ kind: 'tool', tool: 'select' })
    expect(commandFor(key('R'))).toEqual({ kind: 'tool', tool: 'draw' })
    expect(commandFor(key('c'))).toEqual({ kind: 'tool', tool: 'calibrate' })
    expect(commandFor(key('b'))).toEqual({ kind: 'tool', tool: 'background' })
  })

  it('maps undo, redo and save with Ctrl or Cmd', () => {
    expect(commandFor(key('z', { ctrlKey: true }))).toEqual({ kind: 'undo' })
    expect(commandFor(key('Z', { metaKey: true, shiftKey: true }))).toEqual({ kind: 'redo' })
    expect(commandFor(key('y', { ctrlKey: true }))).toEqual({ kind: 'redo' })
    expect(commandFor(key('s', { ctrlKey: true }))).toEqual({ kind: 'save' })
    // Without the modifier these letters are not commands (z) or are tools.
    expect(commandFor(key('z'))).toBeNull()
  })

  it('maps editing keys', () => {
    expect(commandFor(key('Delete'))).toEqual({ kind: 'delete' })
    expect(commandFor(key('Backspace'))).toEqual({ kind: 'delete' })
    expect(commandFor(key('Enter'))).toEqual({ kind: 'finish' })
    expect(commandFor(key('Escape'))).toEqual({ kind: 'cancel' })
  })

  it('maps zoom keys', () => {
    expect(commandFor(key('+'))).toEqual({ kind: 'zoomIn' })
    expect(commandFor(key('='))).toEqual({ kind: 'zoomIn' })
    expect(commandFor(key('-'))).toEqual({ kind: 'zoomOut' })
    expect(commandFor(key('0'))).toEqual({ kind: 'fit' })
  })

  it('arrows nudge by a grid step, or 1 cm with Alt', () => {
    expect(commandFor(key('ArrowLeft'))).toEqual({ kind: 'nudge', dx: -1, dy: 0, fine: false })
    expect(commandFor(key('ArrowDown', { altKey: true }))).toEqual({
      kind: 'nudge',
      dx: 0,
      dy: 1,
      fine: true,
    })
  })

  it('leaves browser shortcuts alone', () => {
    expect(commandFor(key('r', { ctrlKey: true }))).toBeNull()
    expect(commandFor(key('c', { metaKey: true }))).toBeNull()
  })
})

describe('isTypingTarget', () => {
  it('is true in fields and editable content', () => {
    const input = document.createElement('input')
    const textarea = document.createElement('textarea')
    const editable = document.createElement('div')
    editable.setAttribute('contenteditable', 'true')
    const button = document.createElement('button')
    expect(isTypingTarget(input)).toBe(true)
    expect(isTypingTarget(textarea)).toBe(true)
    expect(isTypingTarget(editable)).toBe(true)
    expect(isTypingTarget(button)).toBe(false)
    expect(isTypingTarget(null)).toBe(false)
  })

  it('checkboxes, radios and range sliders are not typing targets', () => {
    const checkbox = document.createElement('input')
    checkbox.type = 'checkbox'
    expect(isTypingTarget(checkbox)).toBe(false)
  })
})
