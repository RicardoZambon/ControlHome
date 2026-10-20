import { useNow } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { capitalize, formatRelative } from '../../../lib/format'
import type { LiveValue } from '../../../stores/realtime'

/** When a live value was observed, relative ("2 minutes ago", "Just now"; refreshed every 30 s), else `undefined`. */
export function useUpdated() {
  const { t, locale } = useI18n()
  const now = useNow({ interval: 30_000 })
  return (live: LiveValue | undefined): string | undefined => {
    if (!live?.observedAtUtc) return undefined
    return capitalize(
      formatRelative(live.observedAtUtc, now.value, locale.value, t('common.justNow')),
      locale.value,
    )
  }
}
