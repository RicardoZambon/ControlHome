import type { EditorTool } from './useEditorState'

/** What a key press means in the floor-plan editor. */
export type EditorCommand =
  | { kind: 'tool'; tool: EditorTool }
  | { kind: 'undo' | 'redo' | 'save' | 'delete' | 'finish' | 'cancel' | 'zoomIn' | 'zoomOut' | 'fit' }
  | { kind: 'nudge'; dx: number; dy: number; fine: boolean }

/** The key shown in tooltips for each tool (and the key that picks it). */
export const TOOL_KEYS: Record<EditorTool, string> = {
  select: 'V',
  draw: 'R',
  calibrate: 'C',
  background: 'B',
}

const TOOLS_BY_KEY: Record<string, EditorTool> = { v: 'select', r: 'draw', c: 'calibrate', b: 'background' }

const ARROWS: Record<string, [number, number]> = {
  ArrowLeft: [-1, 0],
  ArrowRight: [1, 0],
  ArrowUp: [0, -1],
  ArrowDown: [0, 1],
}

type KeyInfo = Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'shiftKey' | 'altKey'>

/** The editor command for a key press, or `null` (left to the browser). */
export function commandFor(event: KeyInfo): EditorCommand | null {
  const key = event.key
  const lower = key.length === 1 ? key.toLowerCase() : key
  const modifier = event.ctrlKey || event.metaKey
  if (modifier) {
    if (lower === 'z') return event.shiftKey ? { kind: 'redo' } : { kind: 'undo' }
    if (lower === 'y') return { kind: 'redo' }
    if (lower === 's') return { kind: 'save' }
    return null
  }
  if (lower in TOOLS_BY_KEY && !event.altKey) return { kind: 'tool', tool: TOOLS_BY_KEY[lower]! }
  if (key === 'Delete' || key === 'Backspace') return { kind: 'delete' }
  if (key === 'Enter') return { kind: 'finish' }
  if (key === 'Escape') return { kind: 'cancel' }
  if (key === '+' || key === '=') return { kind: 'zoomIn' }
  if (key === '-' || key === '_') return { kind: 'zoomOut' }
  if (key === '0') return { kind: 'fit' }
  const arrow = ARROWS[key]
  if (arrow) return { kind: 'nudge', dx: arrow[0], dy: arrow[1], fine: event.altKey }
  return null
}

const NON_TEXT_INPUTS = new Set(['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'color', 'file'])

/** Whether keys typed at `target` are text (a field), so editor shortcuts must not take them. */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false
  if (target instanceof HTMLInputElement) return !NON_TEXT_INPUTS.has(target.type)
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true
  return target.closest('[contenteditable]:not([contenteditable="false"])') !== null
}
