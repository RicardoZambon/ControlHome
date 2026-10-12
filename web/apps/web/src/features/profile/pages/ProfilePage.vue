<script setup lang="ts">
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@controlhome/ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '../../../components/PageHeader.vue'
import UserAvatar from '../../../components/UserAvatar.vue'
import { useSessionStore } from '../../../stores/session'
import RoleBadge from '../../users/components/RoleBadge.vue'
import ChangePasswordForm from '../components/ChangePasswordForm.vue'
import PreferencesForm from '../components/PreferencesForm.vue'

const { t } = useI18n()
const session = useSessionStore()
const me = computed(() => session.me)
</script>

<template>
  <div>
    <PageHeader
      :eyebrow="t('profile.eyebrow')"
      :title="t('profile.title')"
      :description="t('profile.description')"
    />

    <div v-if="me" class="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div class="grid gap-6">
        <Card>
          <CardHeader>
            <CardTitle>{{ t('profile.preferences.title') }}</CardTitle>
            <CardDescription>{{ t('profile.preferences.description') }}</CardDescription>
          </CardHeader>
          <CardContent>
            <PreferencesForm :key="me.id" :me="me" />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{{ t('profile.password.title') }}</CardTitle>
            <CardDescription>{{ t('profile.password.description') }}</CardDescription>
          </CardHeader>
          <CardContent>
            <ChangePasswordForm :user-name="me.userName" />
          </CardContent>
        </Card>
      </div>

      <Card class="order-first gap-0 overflow-hidden pt-0 lg:order-none">
        <div class="h-20 bg-radial-[at_30%_0%] from-accent to-transparent" aria-hidden="true" />
        <CardContent class="-mt-10 grid gap-4">
          <UserAvatar :name="me.displayName" :seed="me.userName" large class="size-16 ring-4 ring-card" />
          <p class="font-serif text-2xl font-medium tracking-tight">{{ me.displayName }}</p>
          <dl class="grid gap-3 border-t pt-4 text-sm">
            <div class="flex items-center justify-between gap-3">
              <dt class="text-muted-foreground">{{ t('profile.account.userName') }}</dt>
              <dd class="truncate font-medium">{{ me.userName }}</dd>
            </div>
            <div class="flex items-center justify-between gap-3">
              <dt class="text-muted-foreground">{{ t('profile.account.roles') }}</dt>
              <dd class="flex flex-wrap justify-end gap-1">
                <RoleBadge v-for="role in me.roles" :key="role" :role="role" />
              </dd>
            </div>
          </dl>
        </CardContent>
      </Card>
    </div>
  </div>
</template>
