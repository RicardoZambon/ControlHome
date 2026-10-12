import { defineConfig } from 'vitest/config'

// Each workspace package owns its Vitest project config; repository scripts get a small node project.
export default defineConfig({
  test: {
    projects: [
      'packages/*/vitest.config.ts',
      'apps/*/vitest.config.ts',
      { test: { name: 'scripts', environment: 'node', include: ['scripts/**/*.test.mjs'] } },
    ],
  },
})
