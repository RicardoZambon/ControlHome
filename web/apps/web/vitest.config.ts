import { fileURLToPath } from 'node:url'
import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config.ts'

export default mergeConfig(
  viteConfig,
  defineConfig({
    server: {
      fs: {
        // The i18n test reads the API's error codes (ErrorCodes.cs) to check every code has a translation.
        allow: [
          fileURLToPath(new URL('../..', import.meta.url)),
          fileURLToPath(new URL('../../../src/ControlHome.Application/Common', import.meta.url)),
        ],
      },
    },
    test: {
      name: 'web',
      environment: 'jsdom',
      include: ['src/**/*.test.ts'],
      setupFiles: ['src/test/setup.ts'],
    },
  }),
)
