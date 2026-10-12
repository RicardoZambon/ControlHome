<script setup lang="ts">
import { deleteUser, listUsers, updateUser, type UserDto } from '@controlhome/api-client'
import {
  Alert,
  AlertDescription,
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  Button,
  Card,
  Dialog,
  DialogScrollContent,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  toast,
} from '@controlhome/ui'
import { RefreshCw, UserRoundPlus } from '@lucide/vue'
import { useNow } from '@vueuse/core'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import UserAvatar from '../../../components/UserAvatar.vue'
import { useProblem } from '../../../composables/useProblem'
import { capitalize, formatDateTime, formatRelative } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'
import ResetUserPasswordForm from '../components/ResetUserPasswordForm.vue'
import RoleBadge from '../components/RoleBadge.vue'
import UserActionsMenu from '../components/UserActionsMenu.vue'
import UserForm from '../components/UserForm.vue'
import UserStatusBadge from '../components/UserStatusBadge.vue'
import { sortUsers, upsertUser } from '../users'

const { t, locale } = useI18n()
const session = useSessionStore()
const problem = useProblem()
const now = useNow({ interval: 60_000 })

const users = ref<UserDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)

const sorted = computed(() => sortUsers(users.value, locale.value))

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    users.value = unwrap(await listUsers())
  } catch (error) {
    loadError.value = error
  } finally {
    loading.value = false
  }
}
onMounted(load)

const isMe = (user: UserDto) => user.id === session.me?.id
const lastLogin = (user: UserDto) =>
  user.lastLoginAtUtc
    ? capitalize(
        formatRelative(user.lastLoginAtUtc, now.value, locale.value, t('common.justNow')),
        locale.value,
      )
    : t('users.never')
const lastLoginTitle = (user: UserDto) =>
  user.lastLoginAtUtc ? formatDateTime(user.lastLoginAtUtc, locale.value) : undefined

// --- Dialogs: create / edit, reset password -------------------------------------------------------------------------
type DialogKind = 'create' | 'edit' | 'reset'
// The content stays in place while the dialog animates out; `dialogOpen` drives the dialog.
const dialog = ref<{ kind: DialogKind; user: UserDto | null } | null>(null)
const dialogOpen = ref(false)

function openDialog(kind: DialogKind, user: UserDto | null): void {
  dialog.value = { kind, user }
  dialogOpen.value = true
}

function onSaved(user: UserDto, created: boolean): void {
  users.value = upsertUser(users.value, user)
  dialogOpen.value = false
  toast.success(t(created ? 'users.toast.created' : 'users.toast.updated', { name: user.displayName }))
  // Your own role or name may have changed.
  if (isMe(user)) void session.loadMe()
}

function onPasswordReset(user: UserDto): void {
  dialogOpen.value = false
  toast.success(t('users.toast.passwordReset', { name: user.displayName }))
}

// --- Confirmations: deactivate, reactivate, delete -------------------------------------------------------------------
type ConfirmKind = 'deactivate' | 'reactivate' | 'delete'
const confirming = ref<{ kind: ConfirmKind; user: UserDto } | null>(null)
const confirmOpen = ref(false)
const confirmBusy = ref(false)
const confirmError = ref('')

function askConfirm(kind: ConfirmKind, user: UserDto): void {
  confirmError.value = ''
  confirming.value = { kind, user }
  confirmOpen.value = true
}

function onConfirmOpenChange(open: boolean): void {
  // Stay open while the request runs.
  confirmOpen.value = open || confirmBusy.value
}

async function runConfirmed(kind: ConfirmKind, user: UserDto): Promise<void> {
  if (kind === 'delete') {
    unwrap(await deleteUser({ path: { id: user.id } }))
    users.value = users.value.filter((existing) => existing.id !== user.id)
    toast.success(t('users.toast.deleted', { name: user.displayName }))
    return
  }
  const active = kind === 'reactivate'
  // The server protects the last active Admin (users.last_admin).
  const updated = unwrap(
    await updateUser({
      path: { id: user.id },
      body: { displayName: user.displayName, role: user.role, isDisabled: !active },
    }),
  )
  users.value = upsertUser(users.value, updated)
  toast.success(t(active ? 'users.toast.enabled' : 'users.toast.disabled', { name: user.displayName }))
}

async function confirm(): Promise<void> {
  const current = confirming.value
  if (!current) return
  confirmBusy.value = true
  confirmError.value = ''
  try {
    await runConfirmed(current.kind, current.user)
    confirmBusy.value = false
    confirmOpen.value = false
  } catch (error) {
    // users.cannot_delete_self, users.last_admin, ...: shown in the dialog.
    confirmError.value = problem.message(error)
  } finally {
    confirmBusy.value = false
  }
}
</script>

<template>
  <div>
    <PageHeader :eyebrow="t('users.eyebrow')" :title="t('users.title')" :description="t('users.description')">
      <template #actions>
        <Button class="max-sm:w-full" @click="openDialog('create', null)">
          <UserRoundPlus />
          {{ t('users.add') }}
        </Button>
      </template>
    </PageHeader>

    <Alert v-if="loadError && !loading" variant="destructive">
      <AlertDescription class="flex flex-wrap items-center justify-between gap-3">
        {{ t('users.loadFailed') }} {{ problem.message(loadError) }}
        <Button variant="outline" size="sm" @click="load">
          <RefreshCw />
          {{ t('common.retry') }}
        </Button>
      </AlertDescription>
    </Alert>

    <template v-else>
      <!-- Desktop: a table. -->
      <Card class="hidden gap-0 overflow-hidden py-0 md:flex">
        <Table>
          <TableCaption class="sr-only">{{ t('users.listLabel') }}</TableCaption>
          <TableHeader>
            <TableRow class="hover:bg-transparent">
              <TableHead class="h-11 pl-6">{{ t('users.user') }}</TableHead>
              <TableHead class="h-11">{{ t('users.role') }}</TableHead>
              <TableHead class="h-11">{{ t('users.status') }}</TableHead>
              <TableHead class="h-11">{{ t('users.lastLogin') }}</TableHead>
              <TableHead class="h-11 w-16 pr-4 text-right">
                <span class="sr-only">{{ t('common.actions') }}</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            <template v-if="loading">
              <TableRow v-for="index in 3" :key="index">
                <TableCell class="py-4 pl-6"><Skeleton class="h-9 w-56" /></TableCell>
                <TableCell><Skeleton class="h-5 w-20" /></TableCell>
                <TableCell><Skeleton class="h-5 w-20" /></TableCell>
                <TableCell><Skeleton class="h-4 w-28" /></TableCell>
                <TableCell class="pr-4" />
              </TableRow>
            </template>
            <TableRow
              v-for="user in sorted"
              v-else
              :key="user.id"
              :data-disabled="user.isDisabled || undefined"
            >
              <TableCell class="py-3 pl-6">
                <div class="flex items-center gap-3">
                  <UserAvatar :name="user.displayName" :seed="user.userName" :inactive="user.isDisabled" />
                  <div class="grid min-w-0 leading-tight">
                    <span class="truncate font-medium">
                      {{ user.displayName }}
                      <span v-if="isMe(user)" class="font-normal text-muted-foreground">
                        {{ t('users.you') }}
                      </span>
                    </span>
                    <span class="truncate text-sm text-muted-foreground">{{ user.userName }}</span>
                  </div>
                </div>
              </TableCell>
              <TableCell><RoleBadge :role="user.role" /></TableCell>
              <TableCell><UserStatusBadge :disabled="user.isDisabled" /></TableCell>
              <TableCell class="text-sm text-muted-foreground">
                <time
                  v-if="user.lastLoginAtUtc"
                  :datetime="user.lastLoginAtUtc"
                  :title="lastLoginTitle(user)"
                >
                  {{ lastLogin(user) }}
                </time>
                <span v-else>{{ t('users.never') }}</span>
              </TableCell>
              <TableCell class="pr-4 text-right">
                <UserActionsMenu
                  :user="user"
                  :self="isMe(user)"
                  :busy="confirmBusy && confirming?.user.id === user.id"
                  @edit="openDialog('edit', user)"
                  @reset-password="openDialog('reset', user)"
                  @toggle-active="askConfirm(user.isDisabled ? 'reactivate' : 'deactivate', user)"
                  @delete="askConfirm('delete', user)"
                />
              </TableCell>
            </TableRow>
          </TableBody>
        </Table>
      </Card>

      <!-- Phones and small tablets: cards. -->
      <ul class="grid gap-3 md:hidden" :aria-label="t('users.listLabel')">
        <template v-if="loading">
          <li v-for="index in 3" :key="index" class="rounded-xl border bg-card p-4">
            <Skeleton class="h-10 w-full" />
          </li>
        </template>
        <li v-for="user in sorted" v-else :key="user.id" class="rounded-xl border bg-card p-4 shadow-xs">
          <div class="flex items-start gap-3">
            <UserAvatar :name="user.displayName" :seed="user.userName" :inactive="user.isDisabled" />
            <div class="grid min-w-0 flex-1 gap-0.5 pt-0.5 leading-tight">
              <span class="truncate font-medium">
                {{ user.displayName }}
                <span v-if="isMe(user)" class="font-normal text-muted-foreground">{{ t('users.you') }}</span>
              </span>
              <span class="truncate text-sm text-muted-foreground">{{ user.userName }}</span>
            </div>
            <UserActionsMenu
              :user="user"
              :self="isMe(user)"
              :busy="confirmBusy && confirming?.user.id === user.id"
              @edit="openDialog('edit', user)"
              @reset-password="openDialog('reset', user)"
              @toggle-active="askConfirm(user.isDisabled ? 'reactivate' : 'deactivate', user)"
              @delete="askConfirm('delete', user)"
            />
          </div>
          <div class="mt-3 flex flex-wrap items-center gap-2 border-t pt-3">
            <RoleBadge :role="user.role" />
            <UserStatusBadge :disabled="user.isDisabled" />
            <span class="ml-auto text-xs text-muted-foreground">
              <template v-if="user.lastLoginAtUtc">
                {{ t('users.lastLogin') }}:
                <time :datetime="user.lastLoginAtUtc">{{ lastLogin(user) }}</time>
              </template>
              <template v-else>{{ t('users.never') }}</template>
            </span>
          </div>
        </li>
      </ul>
    </template>

    <Dialog v-model:open="dialogOpen">
      <DialogScrollContent :close-label="t('common.close')" class="sm:max-w-lg">
        <UserForm
          v-if="dialog?.kind === 'create' || dialog?.kind === 'edit'"
          :key="dialog.user?.id ?? 'new'"
          :user="dialog.user"
          :self="dialog.user ? isMe(dialog.user) : false"
          @saved="onSaved"
        />
        <ResetUserPasswordForm
          v-else-if="dialog?.kind === 'reset' && dialog.user"
          :key="`reset-${dialog.user.id}`"
          :user="dialog.user"
          @done="onPasswordReset"
        />
      </DialogScrollContent>
    </Dialog>

    <AlertDialog :open="confirmOpen" @update:open="onConfirmOpenChange">
      <AlertDialogContent v-if="confirming">
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t(`users.${confirming.kind}.title`, { name: confirming.user.displayName }) }}
          </AlertDialogTitle>
          <AlertDialogDescription>{{ t(`users.${confirming.kind}.description`) }}</AlertDialogDescription>
        </AlertDialogHeader>
        <FormAlert v-if="confirmError" :message="confirmError" />
        <AlertDialogFooter>
          <AlertDialogCancel :disabled="confirmBusy">{{ t('common.cancel') }}</AlertDialogCancel>
          <Button
            :variant="confirming.kind === 'reactivate' ? 'default' : 'destructive'"
            :loading="confirmBusy"
            @click="confirm"
          >
            {{ confirmBusy ? t(`users.${confirming.kind}.busy`) : t(`users.${confirming.kind}.confirm`) }}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
