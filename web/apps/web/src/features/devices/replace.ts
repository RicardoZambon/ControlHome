import type { DeviceSummaryDto } from '@controlhome/api-client'

/**
 * "Replace device" (docs/architecture.md §4 merge rules): an orphaned device, or one reached twice, hands its name,
 * room, icon, placements, favorites and history to another device and is deleted. These are the devices that can take
 * over, the likeliest first.
 */

/** Why a device is offered: the same hardware seen through another integration, the same kind, or any other. */
export type CandidateReason = 'duplicate' | 'sameClass' | 'other'

export interface ReplaceCandidate {
  device: DeviceSummaryDto
  reason: CandidateReason
}

const RANK: Record<CandidateReason, number> = { duplicate: 0, sameClass: 1, other: 2 }

export function replaceCandidates(
  old: Pick<DeviceSummaryDto, 'id' | 'deviceClass' | 'possibleDuplicateIds'>,
  devices: readonly DeviceSummaryDto[],
): ReplaceCandidate[] {
  const duplicates = new Set(old.possibleDuplicateIds)
  return devices
    .filter((device) => device.id !== old.id && !device.isOrphaned)
    .map((device): ReplaceCandidate => ({
      device,
      reason:
        duplicates.has(device.id) || device.possibleDuplicateIds.includes(old.id)
          ? 'duplicate'
          : old.deviceClass !== null && device.deviceClass === old.deviceClass
            ? 'sameClass'
            : 'other',
    }))
    .sort((a, b) => RANK[a.reason] - RANK[b.reason] || a.device.name.localeCompare(b.device.name))
}

/** A device is worth replacing: its integration no longer reports it, or the same hardware is reached twice. */
export function suggestsReplace(
  device: Pick<DeviceSummaryDto, 'isOrphaned' | 'possibleDuplicateIds'>,
): boolean {
  return device.isOrphaned || device.possibleDuplicateIds.length > 0
}
