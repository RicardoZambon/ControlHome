export { default as Toaster } from './Sonner.vue'
// Re-exported so callers use the same vue-sonner instance (and toast queue) as the Toaster.
export { toast, type ExternalToast } from 'vue-sonner'
