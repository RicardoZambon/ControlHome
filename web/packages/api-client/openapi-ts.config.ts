import { defineConfig } from '@hey-api/openapi-ts'

// Generates the typed client from the OpenAPI document that `dotnet build` of ControlHome.Api writes next to this file.
// Run `npm run gen:api` (from web/) after rebuilding the API and commit openapi.json together with src/generated.
export default defineConfig({
  input: './openapi.json',
  output: { path: './src/generated' },
  plugins: ['@hey-api/client-fetch', '@hey-api/typescript', '@hey-api/sdk'],
})
