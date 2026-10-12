/** The app under test (`E2E_BASE_URL`); `scripts/e2e.ps1` sets it to the container it started. */
export const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:5080'

/** The setup code the app under test was started with (`Setup__Code`). */
export const SETUP_CODE = process.env.E2E_SETUP_CODE ?? 'E2E-SETUP-CODE-1'

/** The SQL Server that the app under test (not the browser) connects to in the setup wizard's database step. */
export interface DatabaseSetupTarget {
  host: string
  port: number
  user: string
  password: string
  database: string
}

/**
 * `db-setup.spec.ts` only: `E2E_DB_HOST`, `E2E_DB_PORT`, `E2E_DB_USER`, `E2E_DB_PASSWORD` and `E2E_DB_NAME` (process
 * environment only; `scripts/e2e.ps1 -PreDb` and CI set them). `undefined` unless all are set.
 */
export const DB_SETUP: DatabaseSetupTarget | undefined = (() => {
  const { E2E_DB_HOST, E2E_DB_PORT, E2E_DB_USER, E2E_DB_PASSWORD, E2E_DB_NAME } = process.env
  if (!E2E_DB_HOST || !E2E_DB_PORT || !E2E_DB_USER || !E2E_DB_PASSWORD || !E2E_DB_NAME) return undefined
  return {
    host: E2E_DB_HOST,
    port: Number(E2E_DB_PORT),
    user: E2E_DB_USER,
    password: E2E_DB_PASSWORD,
    database: E2E_DB_NAME,
  }
})()
