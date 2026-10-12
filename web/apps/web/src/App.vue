<script setup lang="ts">
import { Button, Card, CardDescription, CardFooter, CardHeader, CardTitle } from '@controlhome/ui'
</script>

<template>
  <main class="flex min-h-svh items-center justify-center p-4">
    <Card class="w-full max-w-sm">
      <CardHeader>
        <CardTitle>ControlHome</CardTitle>
        <CardDescription>Self-hosted home control.</CardDescription>
      </CardHeader>
      <CardFooter>
        <Button class="w-full">Get started</Button>
      </CardFooter>
    </Card>
  </main>
</template>
