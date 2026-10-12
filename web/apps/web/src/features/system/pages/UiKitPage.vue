<script setup lang="ts">
import {
  Alert,
  AlertDescription,
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
  AlertTitle,
  Badge,
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
  Button,
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
  Checkbox,
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuShortcut,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Label,
  Popover,
  PopoverContent,
  PopoverTrigger,
  Progress,
  RadioGroup,
  RadioGroupItem,
  ScrollArea,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Separator,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
  Skeleton,
  Slider,
  Switch,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  Toggle,
  ToggleGroup,
  ToggleGroupItem,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
  toast,
  useForm,
  z,
} from '@controlhome/ui'
import {
  CircleAlert,
  CircleCheck,
  Copy,
  Info,
  Moon,
  Pencil,
  Plus,
  Sofa,
  Star,
  Sun,
  Trash,
  TriangleAlert,
  WifiOff,
} from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import DeviceStateDot from '../../../components/DeviceStateDot.vue'
import PageHeader from '../../../components/PageHeader.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import SearchableSelect from '../../../components/SearchableSelect.vue'
import UserAvatar from '../../../components/UserAvatar.vue'
import FloorPlanArt from '../../../components/brand/FloorPlanArt.vue'
import { useLocalizedSchema } from '../../../composables/useLocalizedSchema'
import { requiredText } from '../../../lib/validation'
import RoleBadge from '../../users/components/RoleBadge.vue'
import KitSection from '../components/KitSection.vue'
import KitSpecimen from '../components/KitSpecimen.vue'
import TokenSwatches from '../components/TokenSwatches.vue'

const { t } = useI18n()

const sections = [
  'colors',
  'effects',
  'typography',
  'buttons',
  'badges',
  'inputs',
  'choices',
  'feedback',
  'overlays',
  'navigation',
  'data',
] as const

const buttonVariants = ['default', 'secondary', 'outline', 'ghost', 'link', 'destructive'] as const
const buttonSizes = ['xs', 'sm', 'default', 'lg'] as const
const badgeVariants = ['default', 'secondary', 'outline', 'destructive'] as const
// Full class names, so Tailwind generates them.
const shadows = ['shadow-2xs', 'shadow-xs', 'shadow-sm', 'shadow-md', 'shadow-lg', 'shadow-xl', 'shadow-2xl']
const radii = ['rounded-sm', 'rounded-md', 'rounded-lg', 'rounded-xl', 'rounded-2xl', 'rounded-full']
const deviceStates = ['on', 'off', 'offline'] as const
const avatarSizes = ['size-8', 'size-9', 'size-12'] as const
// Each type role with the classes that make it (the spec line is the class list, so it can't drift).
const typeRoles = [
  { role: 'display', sample: 'display', class: 'font-serif text-5xl font-medium tracking-tight' },
  { role: 'h1', sample: 'h1', class: 'font-serif text-3xl font-medium tracking-tight md:text-4xl' },
  { role: 'h2', sample: 'heading', class: 'font-serif text-2xl font-medium tracking-tight' },
  { role: 'h3', sample: 'h3', class: 'font-medium' },
  { role: 'body', sample: 'body', class: 'text-base' },
  { role: 'small', sample: 'small', class: 'text-sm text-muted-foreground' },
  { role: 'caption', sample: 'caption', class: 'text-xs text-muted-foreground' },
  { role: 'eyebrow', sample: 'eyebrow', class: 'eyebrow' },
  { role: 'mono', sample: 'mono', class: 'font-mono text-sm' },
] as const
const scenes = [
  { value: 'morning', icon: Sun },
  { value: 'relax', icon: Sofa },
  { value: 'night', icon: Moon },
] as const

const checked = ref(true)
const switchOn = ref(true)
const scene = ref('relax')
const brightness = ref([64])
const favoriteToggle = ref(false)
const activeScene = ref('relax')
const room = ref('kitchen')
const searchable = ref('kitchen')
const password = ref('')
const favorite = ref(true)
const view = ref('plan')
const roomOptions = [
  { value: 'living', labelKey: 'system.uiKit.sample.rooms.living' },
  { value: 'kitchen', labelKey: 'system.uiKit.sample.rooms.kitchen' },
  { value: 'bedroom', labelKey: 'system.uiKit.sample.rooms.bedroom' },
] as const

// A real vee-validate form with an error, to show the invalid state wiring.
const schema = useLocalizedSchema((t) => z.object({ roomName: requiredText(t, 40) }))
const { handleSubmit } = useForm({ validationSchema: schema, initialValues: { roomName: '' } })
const validate = handleSubmit(() => toast.success(t('system.uiKit.sample.toasts.success')))

function showToast(kind: 'default' | 'success' | 'info' | 'warning' | 'error'): void {
  const message = t(`system.uiKit.sample.toasts.${kind}`)
  const options = { description: t('system.uiKit.sample.toasts.description') }
  if (kind === 'default') toast(message, options)
  else toast[kind](message, options)
}
</script>

<template>
  <div class="grid gap-10">
    <PageHeader
      :eyebrow="t('system.uiKit.eyebrow')"
      :title="t('system.uiKit.title')"
      :description="t('system.uiKit.description')"
      class="pb-0 md:pb-0"
    />

    <nav :aria-label="t('system.uiKit.contents')" class="-mt-4">
      <ul class="flex flex-wrap gap-2">
        <li v-for="section in sections" :key="section">
          <a
            :href="`#kit-${section}`"
            class="inline-flex h-8 items-center rounded-full border bg-card px-3 text-sm text-muted-foreground transition-colors outline-none hover:bg-accent hover:text-accent-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          >
            {{ t(`system.uiKit.sections.${section}`) }}
          </a>
        </li>
      </ul>
    </nav>

    <KitSection id="kit-colors" :title="t('system.uiKit.sections.colors')">
      <TokenSwatches />
    </KitSection>

    <KitSection id="kit-effects" :title="t('system.uiKit.sections.effects')">
      <!-- On the page background, never a lighter tray: raised cards must not look sunken in the dark theme. -->
      <div class="grid grid-cols-2 gap-6 rounded-xl border bg-background p-6 sm:grid-cols-4 lg:grid-cols-7">
        <div v-for="shadow in shadows" :key="shadow" class="grid gap-2 text-center">
          <div :class="['h-16 rounded-lg bg-card', shadow]" />
          <code class="text-xs text-muted-foreground">{{ shadow }}</code>
        </div>
      </div>
      <div class="grid grid-cols-3 gap-4 sm:grid-cols-6">
        <div v-for="radius in radii" :key="radius" class="grid gap-2 text-center">
          <div :class="['h-16 border-2 border-primary bg-accent', radius]" />
          <code class="text-xs text-muted-foreground">{{ radius }}</code>
        </div>
      </div>
      <KitSpecimen :label="t('system.uiKit.groups.rooms')">
        <FloorPlanArt surface-class="fill-card" class="mx-auto max-w-md" />
      </KitSpecimen>
    </KitSection>

    <KitSection id="kit-typography" :title="t('system.uiKit.sections.typography')">
      <dl class="divide-y rounded-xl border bg-card">
        <div
          v-for="entry in typeRoles"
          :key="entry.role"
          class="grid gap-2 p-4 sm:grid-cols-[12rem_minmax(0,1fr)] sm:items-baseline sm:gap-6 sm:px-6"
        >
          <dt class="grid gap-1">
            <span class="text-sm font-medium">{{ t(`system.uiKit.type.roles.${entry.role}`) }}</span>
            <code class="text-xs wrap-break-word text-muted-foreground">{{ entry.class }}</code>
          </dt>
          <dd :class="[entry.class, 'max-w-2xl min-w-0']">{{ t(`system.uiKit.type.${entry.sample}`) }}</dd>
        </div>
      </dl>
    </KitSection>

    <KitSection id="kit-buttons" :title="t('system.uiKit.sections.buttons')">
      <div class="grid gap-4 lg:grid-cols-2">
        <KitSpecimen :label="t('system.uiKit.states.variants')">
          <div class="flex flex-wrap gap-2">
            <Button v-for="variant in buttonVariants" :key="variant" :variant="variant">
              {{ t(`system.uiKit.variants.${variant}`) }}
            </Button>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.states.disabled')">
          <div class="flex flex-wrap gap-2">
            <Button v-for="variant in buttonVariants" :key="variant" :variant="variant" disabled>
              {{ t(`system.uiKit.variants.${variant}`) }}
            </Button>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.states.sizes')">
          <div class="flex flex-wrap items-center gap-2">
            <Button v-for="size in buttonSizes" :key="size" :size="size" variant="outline">
              {{ t(`system.uiKit.sizes.${size}`) }}
            </Button>
          </div>
        </KitSpecimen>
        <KitSpecimen
          :label="`${t('system.uiKit.states.withIcon')} · ${t('system.uiKit.states.iconOnly')} · ${t('system.uiKit.states.loading')}`"
        >
          <div class="flex flex-wrap items-center gap-2">
            <Button><Plus />{{ t('system.uiKit.sample.card.action') }}</Button>
            <Button variant="outline" size="icon" :aria-label="t('system.uiKit.sample.menuEdit')"
              ><Pencil
            /></Button>
            <Button variant="ghost" size="icon-sm" :aria-label="t('system.uiKit.sample.menuDuplicate')"
              ><Copy
            /></Button>
            <Button loading><Plus />{{ t('system.uiKit.states.loading') }}</Button>
            <Button variant="outline" loading>{{ t('system.uiKit.states.loading') }}</Button>
          </div>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-badges" :title="t('system.uiKit.sections.badges')">
      <div class="grid gap-4 lg:grid-cols-2">
        <KitSpecimen :label="t('system.uiKit.states.variants')">
          <div class="flex flex-wrap gap-2">
            <Badge v-for="variant in badgeVariants" :key="variant" :variant="variant">
              {{ t(`system.uiKit.variants.${variant}`) }}
            </Badge>
            <RoleBadge role="Admin" />
            <RoleBadge role="Member" />
            <RoleBadge role="Viewer" />
          </div>
          <div class="flex flex-wrap gap-2">
            <Badge class="border-transparent bg-state-on text-state-on-foreground">{{
              t('system.uiKit.sample.table.on')
            }}</Badge>
            <Badge class="border-transparent bg-state-off text-state-off-foreground">{{
              t('system.uiKit.sample.table.off')
            }}</Badge>
            <Badge class="border-transparent bg-state-offline text-state-offline-foreground"
              ><WifiOff aria-hidden="true" />{{ t('system.uiKit.sample.table.offline') }}</Badge
            >
            <Badge class="border-transparent bg-state-warn text-state-warn-foreground">{{
              t('system.uiKit.sample.toasts.warning')
            }}</Badge>
            <Badge class="border-transparent bg-state-energy text-state-energy-foreground">{{
              t('system.uiKit.sample.energy')
            }}</Badge>
            <Badge class="border-transparent bg-state-danger text-state-danger-foreground">{{
              t('system.uiKit.sample.toasts.error')
            }}</Badge>
            <Badge class="border-transparent bg-state-info text-state-info-foreground">{{
              t('system.uiKit.sample.toasts.info')
            }}</Badge>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.avatars')">
          <div class="flex flex-wrap items-end gap-6">
            <figure v-for="(size, index) in avatarSizes" :key="size" class="grid justify-items-center gap-2">
              <UserAvatar
                :name="['Ana Souza', 'Bruno Lima', 'Carla Dias'][index]!"
                :large="size === 'size-12'"
                :class="size"
              />
              <figcaption>
                <code class="text-xs text-muted-foreground">{{ size }}</code>
              </figcaption>
            </figure>
          </div>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-inputs" :title="t('system.uiKit.sections.inputs')">
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <KitSpecimen :label="t('system.uiKit.states.default')">
          <div class="grid gap-2">
            <Label for="kit-input-default">{{ t('system.uiKit.sample.label') }}</Label>
            <Input id="kit-input-default" :placeholder="t('system.uiKit.sample.placeholder')" />
            <p class="text-sm text-muted-foreground">{{ t('system.uiKit.sample.help') }}</p>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.states.disabled')">
          <div class="grid gap-2">
            <Label for="kit-input-disabled">{{ t('system.uiKit.sample.label') }}</Label>
            <Input id="kit-input-disabled" :model-value="t('system.uiKit.sample.value')" disabled />
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.states.invalid')">
          <div class="grid gap-2">
            <Label for="kit-input-invalid" class="text-destructive">{{
              t('system.uiKit.sample.label')
            }}</Label>
            <Input id="kit-input-invalid" aria-invalid="true" aria-describedby="kit-input-invalid-message" />
            <p id="kit-input-invalid-message" class="text-sm text-destructive">
              {{ t('system.uiKit.sample.invalid') }}
            </p>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.password')">
          <div class="grid gap-2">
            <Label for="kit-password">{{ t('system.uiKit.sample.password') }}</Label>
            <PasswordInput id="kit-password" v-model="password" autocomplete="off" />
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.textarea')">
          <div class="grid gap-2">
            <Label for="kit-textarea">{{ t('system.uiKit.sample.textarea') }}</Label>
            <Textarea id="kit-textarea" :placeholder="t('system.uiKit.sample.help')" />
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.formTitle')">
          <form novalidate class="grid gap-3" @submit="validate">
            <FormField v-slot="{ componentField }" name="roomName">
              <FormItem>
                <FormLabel>{{ t('system.uiKit.sample.label') }}</FormLabel>
                <FormControl><Input v-bind="componentField" /></FormControl>
                <FormDescription>{{ t('system.uiKit.sample.help') }}</FormDescription>
                <FormMessage />
              </FormItem>
            </FormField>
            <Button type="submit" variant="secondary" size="sm" class="justify-self-start">
              {{ t('system.uiKit.sample.formSubmit') }}
            </Button>
          </form>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-choices" :title="t('system.uiKit.sections.choices')">
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <KitSpecimen :label="t('system.uiKit.sample.select')">
          <div class="grid gap-2">
            <Label for="kit-select">{{ t('system.uiKit.sample.select') }}</Label>
            <Select v-model="room">
              <SelectTrigger id="kit-select" class="w-full"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem v-for="option in roomOptions" :key="option.value" :value="option.value">
                  {{ t(option.labelKey) }}
                </SelectItem>
              </SelectContent>
            </Select>
            <Select :model-value="'living'" disabled>
              <SelectTrigger class="w-full" :aria-label="t('system.uiKit.states.disabled')"
                ><SelectValue
              /></SelectTrigger>
              <SelectContent>
                <SelectItem value="living">{{ t('system.uiKit.sample.rooms.living') }}</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.searchable')">
          <SearchableSelect
            v-model="searchable"
            :options="roomOptions.map((option) => ({ value: option.value, label: t(option.labelKey) }))"
            :label="t('system.uiKit.sample.searchable')"
            :placeholder="t('system.uiKit.sample.select')"
            :search-placeholder="t('common.search')"
            :empty-text="t('common.noResults')"
          />
        </KitSpecimen>
        <KitSpecimen
          :label="`${t('system.uiKit.states.checked')} · ${t('system.uiKit.states.unchecked')} · ${t('system.uiKit.states.disabled')}`"
        >
          <div class="grid gap-3">
            <div class="flex items-center gap-3">
              <Checkbox id="kit-check-1" v-model="checked" />
              <Label for="kit-check-1">{{ t('system.uiKit.sample.checkbox') }}</Label>
            </div>
            <div class="flex items-center gap-3">
              <Checkbox id="kit-check-2" />
              <Label for="kit-check-2">{{ t('system.uiKit.sample.checkbox') }}</Label>
            </div>
            <div class="flex items-center gap-3">
              <Checkbox id="kit-check-3" disabled :model-value="true" />
              <Label for="kit-check-3">{{ t('system.uiKit.states.disabled') }}</Label>
            </div>
            <div class="flex items-center gap-3">
              <Checkbox id="kit-check-4" aria-invalid="true" />
              <Label for="kit-check-4" class="text-destructive">{{ t('system.uiKit.states.invalid') }}</Label>
            </div>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.switch')">
          <div class="grid gap-3">
            <div class="flex items-center gap-3">
              <Switch id="kit-switch-1" v-model="switchOn" />
              <Label for="kit-switch-1">{{ t('system.uiKit.sample.switch') }}</Label>
            </div>
            <div class="flex items-center gap-3">
              <Switch id="kit-switch-2" disabled />
              <Label for="kit-switch-2">{{ t('system.uiKit.states.disabled') }}</Label>
            </div>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.radio')">
          <RadioGroup v-model="scene" :aria-label="t('system.uiKit.sample.radio')" class="grid gap-3">
            <div v-for="option in ['relax', 'focus', 'away']" :key="option" class="flex items-center gap-3">
              <RadioGroupItem :id="`kit-radio-${option}`" :value="option" :disabled="option === 'away'" />
              <Label :for="`kit-radio-${option}`">{{
                t(`system.uiKit.sample.radioOptions.${option}`)
              }}</Label>
            </div>
          </RadioGroup>
        </KitSpecimen>
        <KitSpecimen :label="`${t('system.uiKit.sample.slider')} · ${t('system.uiKit.sample.toggleGroup')}`">
          <div class="grid gap-5">
            <div class="grid gap-3">
              <div class="flex items-center justify-between text-sm">
                <Label id="kit-slider-label">{{ t('system.uiKit.sample.slider') }}</Label>
                <span class="text-muted-foreground tabular-nums">{{
                  t('system.uiKit.sample.percent', { value: brightness[0] })
                }}</span>
              </div>
              <Slider v-model="brightness" :max="100" :step="1" aria-labelledby="kit-slider-label" />
            </div>
            <div class="flex flex-wrap items-center gap-3">
              <ToggleGroup
                v-model="activeScene"
                type="single"
                variant="outline"
                :aria-label="t('system.uiKit.sample.toggleGroup')"
              >
                <ToggleGroupItem v-for="option in scenes" :key="option.value" :value="option.value">
                  <component :is="option.icon" />
                  {{ t(`system.uiKit.sample.scenes.${option.value}`) }}
                </ToggleGroupItem>
              </ToggleGroup>
              <Toggle v-model="favoriteToggle" variant="outline" :aria-label="t('system.uiKit.sample.toggle')"
                ><Star
              /></Toggle>
            </div>
          </div>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-feedback" :title="t('system.uiKit.sections.feedback')">
      <div class="grid gap-4 lg:grid-cols-2">
        <Alert>
          <Info />
          <AlertTitle>{{ t('system.uiKit.sample.alertTitle') }}</AlertTitle>
          <AlertDescription>{{ t('system.uiKit.sample.alertDescription') }}</AlertDescription>
        </Alert>
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{{ t('system.uiKit.sample.destructiveTitle') }}</AlertTitle>
          <AlertDescription>{{ t('system.uiKit.sample.destructiveDescription') }}</AlertDescription>
        </Alert>
        <Alert variant="success">
          <CircleCheck />
          <AlertTitle>{{ t('system.uiKit.sample.successTitle') }}</AlertTitle>
          <AlertDescription>{{ t('system.uiKit.sample.successDescription') }}</AlertDescription>
        </Alert>
        <Alert variant="warning">
          <TriangleAlert />
          <AlertTitle>{{ t('system.uiKit.sample.warningTitle') }}</AlertTitle>
          <AlertDescription>{{ t('system.uiKit.sample.warningDescription') }}</AlertDescription>
        </Alert>
        <Alert variant="info" class="lg:col-span-2">
          <Info />
          <AlertTitle>{{ t('system.uiKit.sample.infoTitle') }}</AlertTitle>
          <AlertDescription>{{ t('system.uiKit.sample.infoDescription') }}</AlertDescription>
        </Alert>
        <KitSpecimen :label="`${t('system.uiKit.sample.progress')} · ${t('system.uiKit.states.loading')}`">
          <Progress :model-value="brightness[0]" :aria-label="t('system.uiKit.sample.progress')" />
          <div class="flex items-center gap-3">
            <Skeleton class="size-10 rounded-full" />
            <div class="grid flex-1 gap-2">
              <Skeleton class="h-4 w-3/4" />
              <Skeleton class="h-4 w-1/2" />
            </div>
          </div>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.toast')">
          <div class="flex flex-wrap gap-2">
            <Button variant="outline" size="sm" @click="showToast('default')">{{
              t('system.uiKit.variants.default')
            }}</Button>
            <Button variant="outline" size="sm" @click="showToast('success')">{{
              t('system.uiKit.sample.toasts.success')
            }}</Button>
            <Button variant="outline" size="sm" @click="showToast('info')">{{
              t('system.uiKit.sample.toasts.info')
            }}</Button>
            <Button variant="outline" size="sm" @click="showToast('warning')">{{
              t('system.uiKit.sample.toasts.warning')
            }}</Button>
            <Button variant="outline" size="sm" @click="showToast('error')">{{
              t('system.uiKit.sample.toasts.error')
            }}</Button>
          </div>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-overlays" :title="t('system.uiKit.sections.overlays')">
      <div class="flex flex-wrap gap-3">
        <Dialog>
          <DialogTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.openDialog') }}</Button>
          </DialogTrigger>
          <DialogContent :close-label="t('common.close')">
            <DialogHeader>
              <DialogTitle>{{ t('system.uiKit.sample.dialogTitle') }}</DialogTitle>
              <DialogDescription>{{ t('system.uiKit.sample.dialogDescription') }}</DialogDescription>
            </DialogHeader>
            <div class="grid gap-2">
              <Label for="kit-dialog-input">{{ t('system.uiKit.sample.label') }}</Label>
              <Input id="kit-dialog-input" :model-value="t('system.uiKit.sample.value')" />
            </div>
            <DialogFooter>
              <DialogClose as-child>
                <Button variant="outline">{{ t('common.cancel') }}</Button>
              </DialogClose>
              <DialogClose as-child>
                <Button>{{ t('common.save') }}</Button>
              </DialogClose>
            </DialogFooter>
          </DialogContent>
        </Dialog>

        <AlertDialog>
          <AlertDialogTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.openAlertDialog') }}</Button>
          </AlertDialogTrigger>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>{{ t('system.uiKit.sample.alertDialogTitle') }}</AlertDialogTitle>
              <AlertDialogDescription>{{
                t('system.uiKit.sample.alertDialogDescription')
              }}</AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel>{{ t('common.cancel') }}</AlertDialogCancel>
              <AlertDialogAction class="bg-destructive text-destructive-foreground hover:bg-destructive/90">
                {{ t('common.delete') }}
              </AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>

        <Sheet>
          <SheetTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.openSheet') }}</Button>
          </SheetTrigger>
          <SheetContent :close-label="t('common.close')">
            <SheetHeader>
              <SheetTitle>{{ t('system.uiKit.sample.sheetTitle') }}</SheetTitle>
              <SheetDescription>{{ t('system.uiKit.sample.sheetDescription') }}</SheetDescription>
            </SheetHeader>
            <div class="px-4">
              <FloorPlanArt surface-class="fill-popover" />
            </div>
          </SheetContent>
        </Sheet>

        <Popover>
          <PopoverTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.openPopover') }}</Button>
          </PopoverTrigger>
          <PopoverContent class="text-sm">{{ t('system.uiKit.sample.popover') }}</PopoverContent>
        </Popover>

        <Tooltip>
          <TooltipTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.tooltipTrigger') }}</Button>
          </TooltipTrigger>
          <TooltipContent>{{ t('system.uiKit.sample.tooltip') }}</TooltipContent>
        </Tooltip>

        <DropdownMenu>
          <DropdownMenuTrigger as-child>
            <Button variant="outline">{{ t('system.uiKit.sample.openMenu') }}</Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent class="w-56" align="start">
            <DropdownMenuLabel>{{ t('system.uiKit.sample.menuLabel') }}</DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem>
              <Pencil />
              {{ t('system.uiKit.sample.menuEdit') }}
              <DropdownMenuShortcut>{{ t('system.uiKit.sample.menuShortcut') }}</DropdownMenuShortcut>
            </DropdownMenuItem>
            <DropdownMenuItem disabled>
              <Copy />
              {{ t('system.uiKit.sample.menuDuplicate') }}
            </DropdownMenuItem>
            <DropdownMenuCheckboxItem v-model="favorite">
              <Star />
              {{ t('system.uiKit.sample.menuFavorite') }}
            </DropdownMenuCheckboxItem>
            <DropdownMenuSeparator />
            <DropdownMenuLabel class="text-xs text-muted-foreground">{{
              t('system.uiKit.sample.menuView')
            }}</DropdownMenuLabel>
            <DropdownMenuRadioGroup v-model="view">
              <DropdownMenuRadioItem value="plan">{{
                t('system.uiKit.sample.menuPlan')
              }}</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="list">{{
                t('system.uiKit.sample.menuList')
              }}</DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuSub>
              <DropdownMenuSubTrigger>{{ t('system.uiKit.sample.menuMore') }}</DropdownMenuSubTrigger>
              <DropdownMenuSubContent>
                <DropdownMenuItem>{{ t('system.uiKit.sample.menuExport') }}</DropdownMenuItem>
              </DropdownMenuSubContent>
            </DropdownMenuSub>
            <DropdownMenuItem variant="destructive">
              <Trash />
              {{ t('system.uiKit.sample.menuDelete') }}
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </KitSection>

    <KitSection id="kit-navigation" :title="t('system.uiKit.sections.navigation')">
      <div class="grid gap-4 lg:grid-cols-2">
        <KitSpecimen :label="t('system.uiKit.sections.navigation')">
          <Breadcrumb :label="t('shell.breadcrumb')">
            <BreadcrumbList>
              <BreadcrumbItem>
                <BreadcrumbLink href="#kit-navigation">{{
                  t('system.uiKit.sample.breadcrumbHome')
                }}</BreadcrumbLink>
              </BreadcrumbItem>
              <BreadcrumbSeparator />
              <BreadcrumbItem>
                <BreadcrumbLink href="#kit-navigation">{{
                  t('system.uiKit.sample.breadcrumbFloor')
                }}</BreadcrumbLink>
              </BreadcrumbItem>
              <BreadcrumbSeparator />
              <BreadcrumbItem>
                <BreadcrumbPage>{{ t('system.uiKit.sample.breadcrumbRoom') }}</BreadcrumbPage>
              </BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
          <Separator />
          <Tabs default-value="overview">
            <TabsList>
              <TabsTrigger value="overview">{{ t('system.uiKit.sample.tabs.overview') }}</TabsTrigger>
              <TabsTrigger value="energy">{{ t('system.uiKit.sample.tabs.energy') }}</TabsTrigger>
              <TabsTrigger value="history" disabled>{{ t('system.uiKit.sample.tabs.history') }}</TabsTrigger>
            </TabsList>
            <TabsContent value="overview" class="pt-2 text-sm text-muted-foreground">
              {{ t('system.uiKit.sample.tabs.overviewText') }}
            </TabsContent>
            <TabsContent value="energy" class="pt-2 text-sm text-muted-foreground">
              {{ t('system.uiKit.sample.tabs.energyText') }}
            </TabsContent>
            <TabsContent value="history" class="pt-2 text-sm text-muted-foreground">
              {{ t('system.uiKit.sample.tabs.historyText') }}
            </TabsContent>
          </Tabs>
        </KitSpecimen>
        <KitSpecimen :label="t('system.uiKit.sample.scroll')">
          <ScrollArea class="h-40 rounded-md border">
            <ul class="p-3 text-sm">
              <li v-for="number in 20" :key="number" class="border-b py-1.5 last:border-b-0">
                {{ t('system.uiKit.sample.item', { number }) }}
              </li>
            </ul>
          </ScrollArea>
        </KitSpecimen>
      </div>
    </KitSection>

    <KitSection id="kit-data" :title="t('system.uiKit.sections.data')">
      <div class="grid gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card class="gap-0 overflow-hidden py-0">
          <Table>
            <TableCaption class="pb-3">{{ t('system.uiKit.sample.table.caption') }}</TableCaption>
            <TableHeader>
              <TableRow>
                <TableHead class="pl-4">{{ t('system.uiKit.sample.table.name') }}</TableHead>
                <TableHead>{{ t('system.uiKit.sample.table.room') }}</TableHead>
                <TableHead class="pr-4">{{ t('system.uiKit.sample.table.state') }}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow v-for="(state, index) in deviceStates" :key="state">
                <TableCell class="pl-4 font-medium">{{
                  t('system.uiKit.sample.item', { number: index + 1 })
                }}</TableCell>
                <TableCell>{{ t(roomOptions[index]!.labelKey) }}</TableCell>
                <TableCell class="pr-4">
                  <span class="inline-flex items-center gap-2">
                    <DeviceStateDot :state="state" />
                    {{ t(`system.uiKit.sample.table.${state}`) }}
                  </span>
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>{{ t('system.uiKit.sample.card.title') }}</CardTitle>
            <CardDescription>{{ t('system.uiKit.sample.card.description') }}</CardDescription>
            <CardAction>
              <Button variant="ghost" size="icon-sm" :aria-label="t('system.uiKit.sample.menuEdit')"
                ><Pencil
              /></Button>
            </CardAction>
          </CardHeader>
          <CardContent class="text-sm text-muted-foreground">{{
            t('system.uiKit.sample.card.body')
          }}</CardContent>
          <CardFooter class="justify-end gap-2">
            <Button variant="outline" size="sm">{{ t('common.cancel') }}</Button>
            <Button size="sm">{{ t('system.uiKit.sample.card.action') }}</Button>
          </CardFooter>
        </Card>
      </div>
    </KitSection>
  </div>
</template>
