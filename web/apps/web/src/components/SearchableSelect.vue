<script setup lang="ts">
import { Button, Input, Popover, PopoverContent, PopoverTrigger, cn } from '@controlhome/ui'
import { Check, ChevronsUpDown, Search } from '@lucide/vue'
import { computed, nextTick, ref, useId, watch, type HTMLAttributes } from 'vue'

export interface SearchableOption {
  value: string
  label: string
  /** Secondary text on the right (an offset, a code, ...). */
  hint?: string
  /** Extra words that match the search. */
  keywords?: string
}

/**
 * A select for long lists (time zones, currencies): a button that opens a popover with a search box and a listbox
 * (combobox pattern with aria-activedescendant). Attributes (id, aria-*) go to the button, so it works inside
 * <FormControl>.
 */
defineOptions({ inheritAttrs: false })

const props = defineProps<{
  modelValue?: string
  options: readonly SearchableOption[]
  /** Accessible name of the field (also read with the current value). */
  label: string
  placeholder: string
  searchPlaceholder: string
  emptyText: string
  class?: HTMLAttributes['class']
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string]; blur: [event: Event] }>()

const open = ref(false)
const query = ref('')
const activeIndex = ref(0)
const baseId = useId()
const listId = `${baseId}-list`
const optionId = (index: number) => `${baseId}-option-${index}`

const normalize = (text: string) =>
  text
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .replaceAll('_', ' ')

const searchIndex = computed(() =>
  props.options.map((option) => normalize(`${option.label} ${option.value} ${option.keywords ?? ''}`)),
)

const filtered = computed(() => {
  const words = normalize(query.value).split(/\s+/).filter(Boolean)
  if (words.length === 0) return props.options
  return props.options.filter((_, index) => words.every((word) => searchIndex.value[index]!.includes(word)))
})

const selected = computed(() => props.options.find((option) => option.value === props.modelValue))

function scrollActiveIntoView(): void {
  document.getElementById(optionId(activeIndex.value))?.scrollIntoView({ block: 'nearest' })
}

watch(open, async (isOpen) => {
  if (isOpen) {
    query.value = ''
    activeIndex.value = Math.max(
      0,
      filtered.value.findIndex((option) => option.value === props.modelValue),
    )
    await nextTick()
    scrollActiveIntoView()
  } else {
    // Closing the list counts as leaving the field (vee-validate marks it touched and validates).
    emit('blur', new Event('blur'))
  }
})

watch(query, () => (activeIndex.value = 0))

function choose(option: SearchableOption | undefined): void {
  if (!option) return
  emit('update:modelValue', option.value)
  open.value = false
}

async function move(to: number): Promise<void> {
  const last = filtered.value.length - 1
  if (last < 0) return
  activeIndex.value = Math.min(Math.max(to, 0), last)
  await nextTick()
  scrollActiveIntoView()
}

function onKeydown(event: KeyboardEvent): void {
  const actions: Record<string, () => void> = {
    ArrowDown: () => void move(activeIndex.value + 1),
    ArrowUp: () => void move(activeIndex.value - 1),
    PageDown: () => void move(activeIndex.value + 8),
    PageUp: () => void move(activeIndex.value - 8),
    Enter: () => choose(filtered.value[activeIndex.value]),
  }
  const action = actions[event.key]
  if (!action) return
  event.preventDefault()
  action()
}
</script>

<template>
  <Popover v-model:open="open">
    <!-- Text like Input and SelectTrigger: 16 px on phones (no zoom on focus in iOS Safari), 14 px from md. -->
    <PopoverTrigger as-child>
      <Button
        v-bind="$attrs"
        type="button"
        variant="outline"
        :aria-label="selected ? `${label}: ${selected.label}` : label"
        :class="
          cn(
            'w-full justify-between gap-2 border-input px-3 text-base font-normal shadow-xs aria-invalid:border-destructive md:text-sm',
            !selected && 'text-muted-foreground',
            props.class,
          )
        "
      >
        <span class="truncate">{{ selected?.label ?? placeholder }}</span>
        <span class="ml-auto flex items-center gap-2">
          <span v-if="selected?.hint" class="hidden text-xs text-muted-foreground sm:inline">
            {{ selected.hint }}
          </span>
          <ChevronsUpDown class="text-muted-foreground" />
        </span>
      </Button>
    </PopoverTrigger>
    <PopoverContent align="start" class="w-(--reka-popover-trigger-width) min-w-72 p-0">
      <div class="relative border-b p-2">
        <Search
          class="pointer-events-none absolute top-1/2 left-4.5 size-4 -translate-y-1/2 text-muted-foreground"
        />
        <Input
          v-model="query"
          role="combobox"
          aria-autocomplete="list"
          aria-expanded="true"
          :aria-controls="listId"
          :aria-activedescendant="filtered.length > 0 ? optionId(activeIndex) : undefined"
          :aria-label="searchPlaceholder"
          :placeholder="searchPlaceholder"
          autocomplete="off"
          spellcheck="false"
          class="pl-8"
          @keydown="onKeydown"
        />
      </div>
      <div
        :id="listId"
        role="listbox"
        :aria-label="label"
        class="max-h-72 overflow-y-auto overscroll-contain p-1"
      >
        <div
          v-for="(option, index) in filtered"
          :id="optionId(index)"
          :key="option.value"
          role="option"
          :aria-selected="option.value === modelValue"
          :data-active="index === activeIndex || undefined"
          class="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm select-none data-active:bg-accent data-active:text-accent-foreground"
          @mousedown.prevent
          @mousemove="activeIndex = index"
          @click="choose(option)"
        >
          <Check :class="cn('size-4 shrink-0', option.value === modelValue ? 'text-primary' : 'invisible')" />
          <span class="truncate">{{ option.label }}</span>
          <span v-if="option.hint" class="ml-auto shrink-0 text-xs text-muted-foreground">{{
            option.hint
          }}</span>
        </div>
        <p
          v-if="filtered.length === 0"
          class="px-2 py-6 text-center text-sm text-muted-foreground"
          role="status"
        >
          {{ emptyText }}
        </p>
      </div>
    </PopoverContent>
  </Popover>
</template>
