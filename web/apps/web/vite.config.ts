import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// The dev server proxies the API without rewriting the Host header (changeOrigin: false) so the API's
// same-origin check sees the browser's own origin (http://localhost:5173).
const apiTarget = 'http://localhost:5080'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    // Anchored patterns (a plain '/api' key is a prefix match and would also proxy '/apiary'): only the server-owned
    // segments go to the API, matching the API's SPA fallback (SpaHostingExtensions).
    proxy: {
      '^/api(?:[/?]|$)': { target: apiTarget, changeOrigin: false },
      '^/hubs(?:[/?]|$)': { target: apiTarget, changeOrigin: false, ws: true },
      '^/health(?:[/?]|$)': { target: apiTarget, changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
  },
})
