<script setup lang="ts">
import {
  exportBackup,
  exportBackupWithSecrets,
  getDashboard,
  getScheduledBackup,
  importBackup,
  listScheduledBackups,
  runScheduledBackup,
  type BackupImportResultDto,
  type ScheduledBackupsDto,
} from '@controlhome/api-client'
import { Button, Label, Skeleton, Switch, cn, toast } from '@controlhome/ui'
import {
  ArchiveRestore,
  CalendarClock,
  CircleCheck,
  Download,
  FileArchive,
  Info,
  KeyRound,
  RefreshCw,
  TriangleAlert,
  Upload,
} from '@lucide/vue'
import { useMediaQuery } from '@vueuse/core'
import { computed, onMounted, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import PasswordInput from '../../../components/PasswordInput.vue'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useDevicesStore } from '../../../stores/devices'
import { useHomeStore } from '../../../stores/home'
import {
  PASSPHRASE_MAX,
  PASSPHRASE_MIN,
  backupFileName,
  formatBytes,
  looksLikeBackup,
  passphraseProblem,
  saveBlob,
} from '../backup'

/**
 * Admin: the home's backup. Export downloads a zip of the configuration (floors and their plans, rooms, integrations,
 * devices, places on the plans, accounts without passwords) — with the integrations' credentials too when a passphrase
 * protects them. Restore loads a backup into an empty home (a home with anything in it gets a note instead). The server
 * also writes a backup every day: when it last did, whether that failed, the ones it keeps (to download), and "back up
 * now".
 */
const { t, locale } = useI18n()
const problem = useProblem()
const devices = useDevicesStore()
const homeStore = useHomeStore()
const id = useId()
const touch = useMediaQuery('(pointer: coarse)')

// --- Export ------------------------------------------------------------------------------------------------------------
const withSecrets = ref(false)
const passphrase = ref('')
const confirmation = ref('')
const exportAttempted = ref(false)
const exporting = ref(false)
const exportError = ref('')

const exportProblem = computed(() =>
  withSecrets.value ? passphraseProblem(passphrase.value, confirmation.value) : null,
)
const exportProblemText = computed(() => {
  if (!exportAttempted.value || !exportProblem.value) return ''
  return t(`settings.backup.export.problems.${exportProblem.value}`, {
    min: PASSPHRASE_MIN,
    max: PASSPHRASE_MAX,
  })
})

async function download(): Promise<void> {
  exportAttempted.value = true
  exportError.value = ''
  if (exportProblem.value) return
  exporting.value = true
  try {
    const result = withSecrets.value
      ? await exportBackupWithSecrets({ body: { passphrase: passphrase.value }, parseAs: 'blob' })
      : await exportBackup({ parseAs: 'blob' })
    const blob = unwrap(result) as Blob
    saveBlob(blob, backupFileName(result.response?.headers.get('Content-Disposition') ?? null, new Date()))
    toast.success(
      withSecrets.value ? t('settings.backup.export.doneWithSecrets') : t('settings.backup.export.done'),
    )
    passphrase.value = ''
    confirmation.value = ''
    exportAttempted.value = false
  } catch (error) {
    exportError.value = problem.message(error)
  } finally {
    exporting.value = false
  }
}

// --- Import ------------------------------------------------------------------------------------------------------------
const file = ref<File | null>(null)
const importPassphrase = ref('')
/** The passphrase field shows once a backup turns out to hold credentials (or the admin says it does). */
const askPassphrase = ref(false)
const importing = ref(false)
const importError = ref('')
const result = ref<BackupImportResultDto | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
const dragging = ref(false)
/** The home already has floors, integrations or devices: a backup can't be restored into it. */
const notEmpty = ref(false)

function choose(files: FileList | null | undefined): void {
  const chosen = files?.[0]
  importError.value = ''
  result.value = null
  if (!chosen) return
  if (!looksLikeBackup(chosen)) {
    importError.value = t('settings.backup.import.notZip')
    file.value = null
    return
  }
  file.value = chosen
}

function onDrop(event: DragEvent): void {
  dragging.value = false
  choose(event.dataTransfer?.files)
}

const fileSize = computed(() => (file.value ? formatBytes(file.value.size, locale.value) : ''))

async function restore(): Promise<void> {
  if (!file.value) return
  importing.value = true
  importError.value = ''
  try {
    const body: { file: File; passphrase?: string } = { file: file.value }
    if (importPassphrase.value) body.passphrase = importPassphrase.value
    result.value = unwrap(await importBackup({ body }))
    file.value = null
    importPassphrase.value = ''
    askPassphrase.value = false
    notEmpty.value = true
    void devices.load(true)
    void homeStore.load(true)
    void loadScheduled()
    toast.success(t('settings.backup.import.done'))
  } catch (error) {
    const code = problem.codeOf(error)
    importError.value = problem.message(error)
    if (code === 'backup.passphrase_required' || code === 'backup.passphrase_incorrect')
      askPassphrase.value = true
    if (code === 'backup.not_empty') notEmpty.value = true
  } finally {
    importing.value = false
  }
}

const restored = computed(() => {
  const value = result.value
  if (!value) return []
  return [
    { key: 'floors', count: value.floors },
    { key: 'rooms', count: value.rooms },
    { key: 'integrations', count: value.integrations },
    { key: 'devices', count: value.devices },
    { key: 'placements', count: value.placements },
    { key: 'users', count: value.usersCreated },
  ]
})

// --- Automatic backups ---------------------------------------------------------------------------------------------------
const scheduled = ref<ScheduledBackupsDto | null>(null)
const scheduledError = ref<unknown>(null)
const backingUp = ref(false)
const fetching = ref<string | null>(null)

async function loadScheduled(): Promise<void> {
  try {
    scheduled.value = unwrap(await listScheduledBackups())
    scheduledError.value = null
  } catch (error) {
    scheduledError.value = error
  }
}

async function backUpNow(): Promise<void> {
  backingUp.value = true
  try {
    unwrap(await runScheduledBackup())
    toast.success(t('settings.backup.daily.written'))
  } catch (error) {
    problem.toastError(error)
  } finally {
    backingUp.value = false
    void loadScheduled()
  }
}

async function downloadScheduled(name: string): Promise<void> {
  fetching.value = name
  try {
    const blob = unwrap(await getScheduledBackup({ path: { name }, parseAs: 'blob' })) as Blob
    saveBlob(blob, name)
  } catch (error) {
    problem.toastError(error)
    void loadScheduled()
  } finally {
    fetching.value = null
  }
}

const timeZone = computed(() => homeStore.home?.timeZoneId)
const latest = computed(() => scheduled.value?.files[0] ?? null)

onMounted(async () => {
  void homeStore.load()
  void loadScheduled()
  try {
    const summary = unwrap(await getDashboard())
    notEmpty.value = summary.floorCount > 0 || summary.deviceCount > 0 || summary.integrations.length > 0
  } catch {
    // The server says so on import (409 backup.not_empty).
  }
})
</script>

<template>
  <div>
    <PageHeader
      :eyebrow="t('settings.eyebrow')"
      :title="t('settings.backup.title')"
      :description="t('settings.backup.description')"
    />

    <div class="grid items-start gap-6 lg:grid-cols-2">
      <!-- Export -->
      <section
        class="grid gap-5 rounded-2xl border bg-card p-6 shadow-xs"
        aria-labelledby="backup-export"
        data-slot="backup-export"
      >
        <header class="flex items-start gap-4">
          <span
            class="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground"
            aria-hidden="true"
          >
            <Download class="size-5" />
          </span>
          <div class="grid gap-1">
            <h2 id="backup-export" class="font-serif text-2xl font-medium tracking-tight">
              {{ t('settings.backup.export.title') }}
            </h2>
            <p class="text-sm text-muted-foreground">{{ t('settings.backup.export.description') }}</p>
          </div>
        </header>

        <div
          :class="
            cn(
              'grid gap-4 rounded-xl border p-4 transition-colors',
              withSecrets ? 'border-primary/40 bg-accent/40' : 'bg-muted/30',
            )
          "
        >
          <div class="flex items-start justify-between gap-4">
            <div class="grid gap-1">
              <Label :for="`${id}-secrets`" class="flex items-center gap-2">
                <KeyRound class="size-4 text-muted-foreground" aria-hidden="true" />
                {{ t('settings.backup.export.secrets') }}
              </Label>
              <p class="text-xs text-muted-foreground">{{ t('settings.backup.export.secretsHelp') }}</p>
            </div>
            <Switch :id="`${id}-secrets`" v-model="withSecrets" data-slot="backup-secrets" />
          </div>
          <template v-if="withSecrets">
            <div class="grid gap-2">
              <Label :for="`${id}-passphrase`">{{ t('settings.backup.export.passphrase') }}</Label>
              <PasswordInput
                :id="`${id}-passphrase`"
                v-model="passphrase"
                autocomplete="new-password"
                :maxlength="PASSPHRASE_MAX"
                :aria-invalid="exportProblemText && exportProblem !== 'mismatch' ? 'true' : undefined"
              />
              <p class="text-xs text-muted-foreground">
                {{ t('settings.backup.export.passphraseHelp', { min: PASSPHRASE_MIN }) }}
              </p>
            </div>
            <div class="grid gap-2">
              <Label :for="`${id}-confirm`">{{ t('settings.backup.export.confirm') }}</Label>
              <PasswordInput
                :id="`${id}-confirm`"
                v-model="confirmation"
                autocomplete="new-password"
                :maxlength="PASSPHRASE_MAX"
                :aria-invalid="exportProblem === 'mismatch' && exportProblemText ? 'true' : undefined"
              />
            </div>
            <p v-if="exportProblemText" class="text-sm text-destructive" role="alert">
              {{ exportProblemText }}
            </p>
          </template>
        </div>

        <FormAlert v-if="exportError" :message="exportError" />

        <div class="flex flex-wrap items-center justify-between gap-3">
          <p class="text-xs text-muted-foreground">{{ t('settings.backup.export.noHistory') }}</p>
          <Button :loading="exporting" data-slot="backup-download" @click="download">
            <Download aria-hidden="true" />
            {{ t('settings.backup.export.action') }}
          </Button>
        </div>
      </section>

      <!-- Restore -->
      <section
        class="grid gap-5 rounded-2xl border bg-card p-6 shadow-xs"
        aria-labelledby="backup-import"
        data-slot="backup-import"
      >
        <header class="flex items-start gap-4">
          <span
            class="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground"
            aria-hidden="true"
          >
            <ArchiveRestore class="size-5" />
          </span>
          <div class="grid gap-1">
            <h2 id="backup-import" class="font-serif text-2xl font-medium tracking-tight">
              {{ t('settings.backup.import.title') }}
            </h2>
            <p class="text-sm text-muted-foreground">{{ t('settings.backup.import.description') }}</p>
          </div>
        </header>

        <!-- After a restore: what came back -->
        <div
          v-if="result"
          class="grid gap-4 rounded-xl border border-state-on/40 bg-state-on/5 p-4"
          role="status"
          data-slot="backup-result"
        >
          <p class="flex items-center gap-2 font-medium">
            <CircleCheck class="size-5 text-state-on" aria-hidden="true" />
            {{ t('settings.backup.import.restored') }}
          </p>
          <dl class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
            <div v-for="entry in restored" :key="entry.key" class="grid">
              <dt class="text-muted-foreground">{{ t(`settings.backup.import.counts.${entry.key}`) }}</dt>
              <dd class="font-serif text-xl font-medium tabular-nums">{{ entry.count }}</dd>
            </div>
          </dl>
          <p class="text-sm text-muted-foreground">
            {{
              result.secretsRestored
                ? t('settings.backup.import.secretsBack')
                : t('settings.backup.import.secretsMissing')
            }}
          </p>
          <p v-if="result.usersCreated > 0" class="text-sm text-muted-foreground">
            {{ t('settings.backup.import.usersNote', result.usersCreated) }}
          </p>
          <Button as-child variant="outline" size="sm" class="justify-self-start">
            <RouterLink :to="{ name: 'dashboard' }">{{ t('settings.backup.import.openHome') }}</RouterLink>
          </Button>
        </div>

        <!-- A home with something in it: restoring is for a new server, so just say how -->
        <p
          v-else-if="notEmpty"
          class="flex items-start gap-3 rounded-xl border bg-muted/40 p-4 text-sm text-muted-foreground"
          data-slot="backup-not-empty"
        >
          <Info class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          {{ t('settings.backup.import.notEmpty') }}
        </p>

        <template v-else>
          <!-- The file: drop it here or choose it (phones: the button only) -->
          <div
            :class="
              cn(
                'relative grid justify-items-center gap-2 rounded-xl border-2 border-dashed p-6 text-center transition-colors',
                dragging ? 'border-primary bg-accent/50' : 'bg-muted/30',
              )
            "
            @dragover.prevent="dragging = true"
            @dragleave="dragging = false"
            @drop.prevent="onDrop"
          >
            <FileArchive class="size-8 text-muted-foreground" aria-hidden="true" />
            <p v-if="file" class="text-sm">
              <span class="font-medium">{{ file.name }}</span>
              <span class="text-muted-foreground"> · {{ fileSize }}</span>
            </p>
            <p v-else-if="!touch" class="text-sm text-muted-foreground">
              {{ t('settings.backup.import.drop') }}
            </p>
            <Button variant="outline" size="sm" :disabled="importing" @click="fileInput?.click()">
              {{ file ? t('settings.backup.import.another') : t('settings.backup.import.choose') }}
            </Button>
            <input
              ref="fileInput"
              type="file"
              accept=".zip,application/zip"
              class="sr-only"
              tabindex="-1"
              :aria-label="t('settings.backup.import.choose')"
              data-slot="backup-file"
              @change="choose(($event.target as HTMLInputElement).files)"
            />
          </div>

          <div v-if="askPassphrase" class="grid gap-2" data-slot="backup-import-passphrase">
            <Label :for="`${id}-import-passphrase`">{{ t('settings.backup.import.passphrase') }}</Label>
            <PasswordInput
              :id="`${id}-import-passphrase`"
              v-model="importPassphrase"
              autocomplete="off"
              :maxlength="PASSPHRASE_MAX"
            />
            <p class="text-xs text-muted-foreground">{{ t('settings.backup.import.passphraseHelp') }}</p>
          </div>

          <FormAlert v-if="importError" :message="importError" />

          <div class="flex flex-wrap items-center justify-end gap-2">
            <Button
              v-if="file && !askPassphrase"
              variant="link"
              size="sm"
              class="mr-auto px-0"
              @click="askPassphrase = true"
            >
              <KeyRound aria-hidden="true" />
              {{ t('settings.backup.import.hasCredentials') }}
            </Button>
            <Button :disabled="!file" :loading="importing" data-slot="backup-restore" @click="restore">
              <Upload aria-hidden="true" />
              {{ t('settings.backup.import.action') }}
            </Button>
          </div>
        </template>
      </section>
    </div>

    <!-- Written by the server every day -->
    <section
      class="mt-6 grid gap-4 rounded-2xl border bg-card p-6 shadow-xs"
      aria-labelledby="backup-daily"
      data-slot="backup-scheduled"
      :data-loaded="scheduled ? 'true' : undefined"
    >
      <header class="flex flex-wrap items-start gap-4">
        <span
          class="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent text-accent-foreground"
          aria-hidden="true"
        >
          <CalendarClock class="size-5" />
        </span>
        <!-- At least 14rem wide: on a phone the button moves under the text (to the right) rather than covering it -->
        <div class="grid min-w-0 flex-[1_1_14rem] gap-1">
          <h2 id="backup-daily" class="font-serif text-2xl font-medium tracking-tight">
            {{ t('settings.backup.daily.title') }}
          </h2>
          <p class="text-sm text-muted-foreground">
            {{
              scheduled
                ? t('settings.backup.daily.description', { keep: scheduled.keep })
                : t('settings.backup.daily.description', { keep: 7 })
            }}
          </p>
        </div>
        <Button
          variant="outline"
          class="ml-auto"
          :loading="backingUp"
          data-slot="backup-now"
          @click="backUpNow"
        >
          <RefreshCw aria-hidden="true" />
          {{ t('settings.backup.daily.now') }}
        </Button>
      </header>

      <Skeleton v-if="!scheduled && !scheduledError" class="h-20 rounded-xl" />
      <FormAlert v-else-if="scheduledError && !scheduled" :message="problem.message(scheduledError)" />
      <template v-else-if="scheduled">
        <!-- How it went last time -->
        <p
          v-if="scheduled.lastFailedAtUtc"
          class="flex items-start gap-3 rounded-xl border border-state-warn/50 bg-state-warn/10 p-4 text-sm"
          role="status"
          data-slot="backup-failed"
        >
          <TriangleAlert class="mt-0.5 size-4 shrink-0 text-state-warn" aria-hidden="true" />
          {{
            t('settings.backup.daily.failed', {
              time: formatDateTime(scheduled.lastFailedAtUtc, locale, timeZone),
            })
          }}
        </p>
        <p v-else-if="latest" class="flex items-center gap-2 text-sm" role="status" data-slot="backup-last">
          <CircleCheck class="size-4 shrink-0 text-state-on" aria-hidden="true" />
          {{
            t('settings.backup.daily.last', {
              time: formatDateTime(latest.writtenAtUtc, locale, timeZone),
              count: scheduled.files.length,
            })
          }}
        </p>
        <p v-else class="text-sm text-muted-foreground">{{ t('settings.backup.daily.none') }}</p>

        <ul
          v-if="scheduled.files.length > 0"
          class="grid divide-y rounded-xl border"
          data-slot="backup-files"
        >
          <li
            v-for="entry in scheduled.files"
            :key="entry.name"
            class="flex items-center gap-3 px-4 py-2.5 text-sm"
          >
            <FileArchive class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span class="min-w-0 flex-1 truncate tabular-nums">
              {{ formatDateTime(entry.writtenAtUtc, locale, timeZone) }}
              <span class="text-muted-foreground"> · {{ formatBytes(entry.sizeBytes, locale) }}</span>
            </span>
            <Button
              variant="ghost"
              size="sm"
              :loading="fetching === entry.name"
              :aria-label="
                t('settings.backup.daily.download', {
                  time: formatDateTime(entry.writtenAtUtc, locale, timeZone),
                })
              "
              @click="downloadScheduled(entry.name)"
            >
              <Download aria-hidden="true" />
              <span class="max-sm:sr-only">{{ t('settings.backup.daily.downloadShort') }}</span>
            </Button>
          </li>
        </ul>
        <p class="text-xs text-muted-foreground">{{ t('settings.backup.daily.noCredentials') }}</p>
      </template>
    </section>
  </div>
</template>
