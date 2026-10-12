/** The app under test (`E2E_BASE_URL`); `scripts/e2e.ps1` sets it to the container it started. */
export const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:5080'

/** The setup code the app under test was started with (`Setup__Code`). */
export const SETUP_CODE = process.env.E2E_SETUP_CODE ?? 'E2E-SETUP-CODE-1'
