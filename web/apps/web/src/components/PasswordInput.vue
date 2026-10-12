<script setup lang="ts">
import { Button, Input, cn } from '@controlhome/ui'
import { Eye, EyeOff } from '@lucide/vue'
import { ref, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * A password field with a show/hide button. Attributes (id, name, autocomplete, aria-*) go to the <input>, so it
 * works inside <FormControl>.
 */
defineOptions({ inheritAttrs: false })

const props = defineProps<{ modelValue?: string; class?: HTMLAttributes['class'] }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const { t } = useI18n()
const visible = ref(false)
</script>

<template>
  <div :class="cn('relative', props.class)">
    <Input
      v-bind="$attrs"
      :type="visible ? 'text' : 'password'"
      :model-value="props.modelValue ?? ''"
      class="pr-10"
      spellcheck="false"
      autocapitalize="off"
      @update:model-value="(value) => emit('update:modelValue', String(value))"
    />
    <Button
      type="button"
      variant="ghost"
      size="icon-sm"
      class="absolute top-1/2 right-0.5 -translate-y-1/2 text-muted-foreground"
      :aria-label="visible ? t('common.hidePassword') : t('common.showPassword')"
      :aria-pressed="visible"
      @click="visible = !visible"
    >
      <EyeOff v-if="visible" />
      <Eye v-else />
    </Button>
  </div>
</template>
