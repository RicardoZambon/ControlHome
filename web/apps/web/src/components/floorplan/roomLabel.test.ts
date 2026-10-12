import { describe, expect, it } from 'vitest'
import { roomLabelExtent, roomLabelLayout } from './roomLabel'

describe('roomLabelExtent', () => {
  it('is the space the label takes around its point (px): as wide as its widest line', () => {
    const layout = roomLabelLayout({ name: 'Kitchen', widthPx: 420, heightPx: 320 })!
    const extent = roomLabelExtent(layout, '72 m²')
    expect(extent.top).toBeCloseTo(layout.lines[0]!.top)
    expect(extent.bottom).toBeCloseTo(-extent.top)
    // "Kitchen" at 16 px is wider than "72 m²" at 13 px and the 18 px icon.
    expect(extent.halfWidth).toBeCloseTo((7 * 16 * 0.55) / 2)
  })
})

const kinds = (layout: ReturnType<typeof roomLabelLayout>) => layout?.lines.map((line) => line.kind)

describe('roomLabelLayout', () => {
  it('grows the label with the room on screen (13 → 16 px names)', () => {
    const small = roomLabelLayout({ name: 'Kitchen', widthPx: 150, heightPx: 100 })
    const large = roomLabelLayout({ name: 'Kitchen', widthPx: 420, heightPx: 320 })
    expect(small).toMatchObject({ nameSize: 13, areaSize: 11 })
    expect(large).toMatchObject({ nameSize: 16, areaSize: 13 })
    expect(kinds(large)).toEqual(['icon', 'name', 'area'])
  })

  it('drops the icon, then the area, then everything as the room gets small', () => {
    expect(kinds(roomLabelLayout({ name: 'Bath', widthPx: 120, heightPx: 60 }))).toEqual(['name', 'area'])
    expect(kinds(roomLabelLayout({ name: 'Bath', widthPx: 120, heightPx: 30 }))).toEqual(['name'])
    expect(roomLabelLayout({ name: 'Bath', widthPx: 30, heightPx: 30 })).toBeNull()
  })

  it('shortens a name that does not fit, with an ellipsis', () => {
    const layout = roomLabelLayout({ name: 'A very long room name indeed', widthPx: 90, heightPx: 90 })
    expect(layout!.name.endsWith('…')).toBe(true)
    expect(layout!.name.length).toBeLessThan(28)
  })

  it('previews (compact) show small names and the icon, never the area', () => {
    const layout = roomLabelLayout({ name: 'Kitchen', widthPx: 200, heightPx: 120, compact: true })
    expect(layout).toMatchObject({ nameSize: 11 })
    expect(kinds(layout)).toEqual(['icon', 'name'])
    expect(kinds(roomLabelLayout({ name: 'Kitchen', widthPx: 200, heightPx: 50, compact: true }))).toEqual([
      'name',
    ])
  })
})
