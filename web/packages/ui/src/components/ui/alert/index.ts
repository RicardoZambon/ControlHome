import type { VariantProps } from 'class-variance-authority'
import { cva } from 'class-variance-authority'

export { default as Alert } from './Alert.vue'
export { default as AlertDescription } from './AlertDescription.vue'
export { default as AlertTitle } from './AlertTitle.vue'

// The colored variants color the icon and the edge (and the title of destructive, which reaches 4.5:1 on the
// surfaces); the body stays in --foreground so it reads easily. Success, warning and info sit on a light tint of
// their state color.
const colored = '*:data-[slot=alert-description]:text-foreground'

export const alertVariants = cva(
  'relative w-full rounded-lg border px-4 py-3 text-sm grid has-[>svg]:grid-cols-[calc(var(--spacing)*4)_1fr] grid-cols-[0_1fr] has-[>svg]:gap-x-3 gap-y-0.5 items-start [&>svg]:size-4 [&>svg]:translate-y-0.5 [&>svg]:text-current',
  {
    variants: {
      variant: {
        default: 'bg-card text-card-foreground',
        destructive: `bg-card text-card-foreground border-destructive/50 [&>svg]:text-destructive *:data-[slot=alert-title]:text-destructive ${colored}`,
        success: `bg-state-on/10 text-card-foreground border-state-on/40 [&>svg]:text-state-on ${colored}`,
        warning: `bg-state-warn/10 text-card-foreground border-state-warn/50 [&>svg]:text-state-warn ${colored}`,
        info: `bg-state-info/10 text-card-foreground border-state-info/40 [&>svg]:text-state-info ${colored}`,
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
)

export type AlertVariants = VariantProps<typeof alertVariants>
