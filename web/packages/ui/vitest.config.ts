import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [vue()],
  test: {
    name: 'ui',
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    // Vitest empties CSS imports by default; the theme composables read tokens.css (`?raw`) for the token names.
    css: { include: [/\/styles\/tokens\.css/] },
  },
})
