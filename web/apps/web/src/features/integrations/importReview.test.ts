import { describe, expect, it } from 'vitest'
import { basicHome } from '../../test/devices'
import { initialChoice, reviewChanges, roomSuggestion, suggestRoom, type ReviewChoice } from './importReview'

const ROOMS = [
  { id: 1, name: 'Living room' },
  { id: 2, name: 'Bedroom' },
  { id: 3, name: 'Master bedroom' },
  { id: 4, name: 'Escritório' },
  { id: 5, name: 'Room' },
]

describe('suggestRoom', () => {
  it('finds the room named in the device name, by whole words', () => {
    expect(suggestRoom('Living room light', ROOMS)).toBe(1)
    expect(suggestRoom('Bedroom air conditioner', ROOMS)).toBe(2)
    expect(suggestRoom('Front door lock', ROOMS)).toBeNull()
    // "Bedroom" is not "Room".
    expect(suggestRoom('Bedroomlamp', ROOMS)).toBeNull()
  })

  it('prefers the longest room name', () => {
    expect(suggestRoom('Master bedroom lamp', ROOMS)).toBe(3)
  })

  it('ignores case, accents and punctuation', () => {
    expect(suggestRoom('Luz do escritorio', ROOMS)).toBe(4)
    expect(suggestRoom('LIVING-ROOM TV', ROOMS)).toBe(1)
  })
})

describe('roomSuggestion', () => {
  it('takes the room the integration put the device in, else the one its name points to', () => {
    const [light, , , contact] = basicHome()
    expect(roomSuggestion(light!, ROOMS)).toEqual({ roomId: 10, source: 'integration' })
    expect(roomSuggestion({ ...contact!, name: 'Bedroom window' }, ROOMS)).toEqual({
      roomId: 2,
      source: 'name',
    })
    expect(roomSuggestion(contact!, ROOMS)).toBeNull()
  })
})

describe('review choices', () => {
  it('keeps every device in its own room, else the suggested one', () => {
    const [light, , , contact] = basicHome()
    expect(initialChoice(light!, ROOMS)).toEqual({ keep: true, roomId: 10 })
    expect(initialChoice({ ...contact!, name: 'Bedroom window' }, ROOMS)).toEqual({ keep: true, roomId: 2 })
    expect(initialChoice(contact!, ROOMS)).toEqual({ keep: true, roomId: null })
  })

  it('changes only what differs: rooms of kept devices, hidden endpoints of the others', () => {
    const devices = basicHome()
    const choices = new Map<string, ReviewChoice>([
      ['d-light', { keep: true, roomId: 10 }],
      ['d-plug', { keep: true, roomId: 12 }],
      ['d-contact', { keep: false, roomId: null }],
      ['d-lock', { keep: true, roomId: null }],
    ])
    const changes = reviewChanges(devices, choices)
    expect(changes.map((change) => [change.kind, change.device.id])).toEqual([
      ['room', 'd-plug'],
      ['hide', 'd-contact'],
    ])
    expect(changes[0]).toMatchObject({ roomId: 12 })
  })

  it('does not hide a device that is already hidden', () => {
    const [light] = basicHome()
    const hidden = {
      ...light!,
      endpoints: light!.endpoints.map((endpoint) => ({ ...endpoint, hidden: true })),
    }
    expect(reviewChanges([hidden], new Map([['d-light', { keep: false, roomId: null }]]))).toEqual([])
  })
})
