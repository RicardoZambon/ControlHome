<script setup lang="ts">
import { Button, Input, cn } from '@controlhome/ui'
import { Eye, EyeOff } from '@lucide/vue'
import { ref, type HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * A password field with a show/hide button. Attributes (id, name, autocomplete, aria-*) go to the <input>, so it
 * works inside <FormControl>. The button is as tall as the field and square (44 px on touch screens).
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
      class="pr-control"
      spellcheck="false"
      autocapitalize="off"
      @update:model-value="(value) => emit('update:modelValue', String(value))"
    />
    <Button
      type="button"
      variant="ghost"
      size="icon"
      class="absolute top-0 right-0 rounded-l-none text-muted-foreground hover:bg-transparent hover:text-foreground"
      :aria-label="visible ? t('common.hidePassword') : t('common.showPassword')"
      :aria-pressed="visible"
      @click="visible = !visible"
    >
      <EyeOff v-if="visible" />
      <Eye v-else />
    </Button>
  </div>
</template>
