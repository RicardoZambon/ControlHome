<script setup lang="ts">
import { Badge } from '@controlhome/ui'
import { Eye, ShieldCheck, UserRound } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{ role: string }>()
const { t } = useI18n()

const styles = {
  Admin: {
    icon: ShieldCheck,
    variant: 'default',
    class: 'border-transparent bg-accent text-accent-foreground',
  },
  Member: { icon: UserRound, variant: 'secondary', class: '' },
  Viewer: { icon: Eye, variant: 'outline', class: 'text-muted-foreground' },
} as const

const style = computed(() => styles[props.role as keyof typeof styles] ?? styles.Viewer)
const label = computed(() => (props.role in styles ? t(`roles.${props.role}`) : props.role))
</script>

<template>
  <Badge :variant="style.variant" :class="style.class">
    <component :is="style.icon" aria-hidden="true" />
    {{ label }}
  </Badge>
</template>
