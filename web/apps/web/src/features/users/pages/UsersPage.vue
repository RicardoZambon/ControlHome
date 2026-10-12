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
  Badge,
  Button,
  Card,
  Dialog,
  DialogScrollContent,
  Skeleton,
  Switch,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  toast,
} from '@controlhome/ui'
import { LoaderCircle, RefreshCw, UserRoundPlus } from '@lucide/vue'
import { useNow } from '@vueuse/core'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import FormAlert from '../../../components/FormAlert.vue'
import PageHeader from '../../../components/PageHeader.vue'
import UserAvatar from '../../../components/UserAvatar.vue'
import { useProblem } from '../../../composables/useProblem'
import { formatDateTime, formatRelative } from '../../../lib/format'
import { unwrap } from '../../../lib/problem'
import { useSessionStore } from '../../../stores/session'
import ResetUserPasswordForm from '../components/ResetUserPasswordForm.vue'
import RoleBadge from '../components/RoleBadge.vue'
import UserActionsMenu from '../components/UserActionsMenu.vue'
import UserForm from '../components/UserForm.vue'
import { sortUsers, upsertUser } from '../users'

const { t, locale } = useI18n()
const session = useSessionStore()
const problem = useProblem()
const now = useNow({ interval: 60_000 })

const users = ref<UserDto[]>([])
const loading = ref(true)
const loadError = ref<unknown>(null)
const pending = ref<string | null>(null)

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
  user.lastLoginAtUtc ? formatRelative(user.lastLoginAtUtc, now.value, locale.value) : t('users.never')
const lastLoginTitle = (user: UserDto) =>
  user.lastLoginAtUtc ? formatDateTime(user.lastLoginAtUtc, locale.value) : undefined

// --- Dialogs: create / edit, reset password, delete ------------------------------------------------------------------
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

const deleting = ref<UserDto | null>(null)
const deleteBusy = ref(false)
const deleteError = ref('')
const deleteOpen = ref(false)

function askDelete(user: UserDto): void {
  deleteError.value = ''
  deleting.value = user
  deleteOpen.value = true
}

function onDeleteOpenChange(open: boolean): void {
  // Stay open while the request runs.
  deleteOpen.value = open || deleteBusy.value
}

async function confirmDelete(): Promise<void> {
  const user = deleting.value
  if (!user) return
  deleteBusy.value = true
  deleteError.value = ''
  try {
    unwrap(await deleteUser({ path: { id: user.id } }))
    users.value = users.value.filter((existing) => existing.id !== user.id)
    toast.success(t('users.toast.deleted', { name: user.displayName }))
    deleteBusy.value = false
    deleteOpen.value = false
  } catch (error) {
    // users.cannot_delete_self, users.last_admin, ...: shown in the dialog.
    deleteError.value = problem.message(error)
  } finally {
    deleteBusy.value = false
  }
}

/** Enables or disables an account (the server protects the last active Admin: users.last_admin). */
async function setActive(user: UserDto, active: boolean): Promise<void> {
  if (pending.value) return
  pending.value = user.id
  try {
    const updated = unwrap(
      await updateUser({
        path: { id: user.id },
        body: { displayName: user.displayName, role: user.role, isDisabled: !active },
      }),
    )
    users.value = upsertUser(users.value, updated)
    toast.success(t(active ? 'users.toast.enabled' : 'users.toast.disabled', { name: user.displayName }))
  } catch (error) {
    problem.toastError(error)
  } finally {
    pending.value = null
  }
}
</script>

<template>
  <div>
    <PageHeader :eyebrow="t('users.eyebrow')" :title="t('users.title')" :description="t('users.description')">
      <template #actions>
        <Button @click="openDialog('create', null)">
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
              <TableHead class="h-11 w-16 pr-6 text-right">
                <span class="sr-only">{{ t('common.actions') }}</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            <template v-if="loading">
              <TableRow v-for="index in 3" :key="index">
                <TableCell class="py-4 pl-6"><Skeleton class="h-9 w-56" /></TableCell>
                <TableCell><Skeleton class="h-5 w-20" /></TableCell>
                <TableCell><Skeleton class="h-5 w-24" /></TableCell>
                <TableCell><Skeleton class="h-4 w-28" /></TableCell>
                <TableCell class="pr-6" />
              </TableRow>
            </template>
            <TableRow
              v-for="user in sorted"
              v-else
              :key="user.id"
              :data-disabled="user.isDisabled || undefined"
            >
              <TableCell class="py-3.5 pl-6">
                <div class="flex items-center gap-3">
                  <UserAvatar
                    :name="user.displayName"
                    :seed="user.userName"
                    :class="user.isDisabled ? 'opacity-50 grayscale' : ''"
                  />
                  <div class="grid min-w-0 leading-tight">
                    <span class="flex items-center gap-2 truncate font-medium">
                      {{ user.displayName }}
                      <Badge v-if="isMe(user)" variant="outline" class="text-muted-foreground">
                        {{ t('users.you') }}
                      </Badge>
                    </span>
                    <span class="truncate text-sm text-muted-foreground">{{ user.userName }}</span>
                  </div>
                </div>
              </TableCell>
              <TableCell><RoleBadge :role="user.role" /></TableCell>
              <TableCell>
                <div class="flex items-center gap-2.5">
                  <Switch
                    :model-value="!user.isDisabled"
                    :disabled="pending === user.id"
                    :aria-label="t('users.activeToggle', { name: user.displayName })"
                    @update:model-value="(active) => setActive(user, active === true)"
                  />
                  <span :class="user.isDisabled ? 'text-sm text-muted-foreground' : 'text-sm'">
                    {{ user.isDisabled ? t('users.disabled') : t('users.active') }}
                  </span>
                  <LoaderCircle
                    v-if="pending === user.id"
                    class="size-4 animate-spin text-muted-foreground"
                  />
                </div>
              </TableCell>
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
              <TableCell class="pr-6 text-right">
                <UserActionsMenu
                  :user="user"
                  :busy="pending === user.id"
                  @edit="openDialog('edit', user)"
                  @reset-password="openDialog('reset', user)"
                  @toggle-active="setActive(user, user.isDisabled)"
                  @delete="askDelete(user)"
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
            <UserAvatar
              :name="user.displayName"
              :seed="user.userName"
              :class="user.isDisabled ? 'opacity-50 grayscale' : ''"
            />
            <div class="grid min-w-0 flex-1 gap-0.5 leading-tight">
              <span class="flex flex-wrap items-center gap-2 font-medium">
                <span class="truncate">{{ user.displayName }}</span>
                <Badge v-if="isMe(user)" variant="outline" class="text-muted-foreground">{{
                  t('users.you')
                }}</Badge>
              </span>
              <span class="truncate text-sm text-muted-foreground">{{ user.userName }}</span>
            </div>
            <UserActionsMenu
              :user="user"
              :busy="pending === user.id"
              @edit="openDialog('edit', user)"
              @reset-password="openDialog('reset', user)"
              @toggle-active="setActive(user, user.isDisabled)"
              @delete="askDelete(user)"
            />
          </div>
          <div class="mt-4 flex flex-wrap items-center justify-between gap-3 border-t pt-3">
            <RoleBadge :role="user.role" />
            <div class="flex items-center gap-2">
              <span class="text-sm text-muted-foreground">
                {{ user.isDisabled ? t('users.disabled') : t('users.active') }}
              </span>
              <Switch
                :model-value="!user.isDisabled"
                :disabled="pending === user.id"
                :aria-label="t('users.activeToggle', { name: user.displayName })"
                @update:model-value="(active) => setActive(user, active === true)"
              />
            </div>
          </div>
          <p class="mt-2 text-xs text-muted-foreground">
            <template v-if="user.lastLoginAtUtc">
              {{ t('users.lastLogin') }}:
              <time :datetime="user.lastLoginAtUtc">{{ lastLogin(user) }}</time>
            </template>
            <template v-else>{{ t('users.never') }}</template>
          </p>
        </li>
      </ul>
    </template>

    <Dialog v-model:open="dialogOpen">
      <DialogScrollContent :close-label="t('common.close')" class="outline-none sm:max-w-lg">
        <UserForm
          v-if="dialog?.kind === 'create' || dialog?.kind === 'edit'"
          :key="dialog.user?.id ?? 'new'"
          :user="dialog.user"
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

    <AlertDialog :open="deleteOpen" @update:open="onDeleteOpenChange">
      <AlertDialogContent v-if="deleting">
        <AlertDialogHeader>
          <AlertDialogTitle class="font-serif text-2xl font-medium tracking-tight">
            {{ t('users.delete.title', { name: deleting.displayName }) }}
          </AlertDialogTitle>
          <AlertDialogDescription>{{ t('users.delete.description') }}</AlertDialogDescription>
        </AlertDialogHeader>
        <FormAlert v-if="deleteError" :message="deleteError" />
        <AlertDialogFooter>
          <AlertDialogCancel :disabled="deleteBusy">{{ t('common.cancel') }}</AlertDialogCancel>
          <Button variant="destructive" :disabled="deleteBusy" @click="confirmDelete">
            <LoaderCircle v-if="deleteBusy" class="animate-spin" />
            {{ deleteBusy ? t('users.delete.deleting') : t('users.delete.confirm') }}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
