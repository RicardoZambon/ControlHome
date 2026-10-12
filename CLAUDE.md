# ControlHome — agent guide

Self-hosted home control + automation web app. Backend .NET 10 (ASP.NET Core minimal APIs, EF Core 10, SQL Server 2025),
frontend Vue 3 (Vite, TypeScript, Tailwind 4 + shadcn-vue), shipped as one Docker image for a LAN (Umbrel). Public repo.

- Living architecture: `docs/architecture.md` · decisions: `docs/adr/` · roadmap: `docs/roadmap.md`
- Current progress / resume point: `docs/STATUS.md` · per-phase task plans: `docs/plans/`

## Commands
```bash
dotnet build -warnaserror          # whole solution (ControlHome.slnx)
dotnet test                        # unit + architecture + integration (needs Docker or CONTROLHOME_TEST_SQLSERVER)
cd web && npm ci                   # Node 24 (nvm use 24.21.0)
npm run lint && npm run typecheck && npm test && npm run check:colors && npm run build
npm run gen:api                    # regenerate @controlhome/api-client from web/packages/api-client/openapi.json
```
`dotnet build` of `ControlHome.Api` regenerates `web/packages/api-client/openapi.json`; commit it together with the regenerated client.

## Backend rules
- Reference graph (enforced by `tests/ControlHome.Architecture.Tests`): `Domain` (no deps) ← `Application` ← `Infrastructure` ← `Api`.
  Later: `Connectors.Abstractions` → Domain; `Connectors.<Vendor>` → Abstractions only; `Automation` → Domain only.
- No MediatR / AutoMapper / FluentAssertions / Moq. Plain services + DI. Map by hand.
- DTOs are `record`s defined **once** in `ControlHome.Application/<Feature>/…Contracts.cs`. No duplicate DTOs in Api.
- Application services use `IControlHomeDb` (DbSets + SaveChanges) directly — no per-aggregate repository/unit-of-work layers.
- Endpoints: one static class per feature in `ControlHome.Api/Features/<Feature>/<Feature>Endpoints.cs` with
  `Map<Feature>Endpoints(this IEndpointRouteBuilder)`; return `TypedResults`; validation via `ValidationFilter<T>` (FluentValidation).
- Services registered per feature (`services.Add<Feature>()`), so shared files only get one-line additions.
- Errors: ProblemDetails with extension `code` (dotted, stable, e.g. `users.last_admin`). Never leak exception text outside Development.
- Nothing before `builder.Build()` may open a DB or network connection (build-time OpenAPI generation runs Program.cs).
- Time: inject `TimeProvider` (never `DateTime.Now`/`UtcNow` in logic). Store UTC (`DateTimeOffset`/`datetime2`).
- Logging: structured Serilog templates, no secrets, no PII beyond user names. Never log connection strings or tokens.
- Tests: xunit.v3 + Shouldly + NSubstitute. Integration tests use `WebApplicationFactory<Program>` + the shared SQL fixture
  (Testcontainers `mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04`, or `CONTROLHOME_TEST_SQLSERVER` fallback).
- EF migrations are created **only by the integrator** (`dotnet ef migrations add <Name> -p src/ControlHome.Infrastructure -s src/ControlHome.Api -o Persistence/Migrations`).
  Implementers change entities/configurations and say a migration is needed.
- Package versions live only in `Directory.Packages.props` (and `web/package-lock.json`); implementers list new packages in
  their report and may add them, but never bump unrelated versions.

## Frontend rules (`web/`)
- **One design source.** Colors exist only in `web/packages/ui/src/styles/tokens.css` (OKLCH CSS variables, `:root` + `.dark`).
  Tailwind's default palette is disabled; use semantic classes (`bg-primary`, `text-muted-foreground`, `bg-state-on`, …).
  No hex/rgb/hsl/oklch literals anywhere else (`npm run check:colors`), no arbitrary color values.
- Primitives come from `@controlhome/ui` (shadcn-vue components live in `web/packages/ui/src/components/ui`). Pages never
  re-implement a primitive; shared domain components live in `web/apps/web/src/components/`.
- Features are folders `web/apps/web/src/features/<feature>/` with `routes.ts`, optional `nav.ts`, `locales/en.json`,
  `locales/pt-BR.json`, pages and feature components — auto-registered via `import.meta.glob`.
- Every user-visible string uses vue-i18n with keys present in **both** `en` and `pt-BR` (missing keys throw in tests).
- API calls only through `@controlhome/api-client` (generated). Error messages: `t('errors.' + problem.code)`.
- Vue: `<script setup lang="ts">`, Composition API, Pinia setup stores, no Options API.

## Security rules (public repo, real home)
- Never commit secrets. Dev DB connection string lives in user-secrets (`src/ControlHome.Api`); compose secrets in gitignored `.env`.
  The pre-commit hook (`scripts/check-secrets.sh`, enable with `git config core.hooksPath .githooks`) blocks known values.
- Databases: only names matching `^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$` may be created/dropped by tools or tests
  (`DatabaseNameGuard`). The SQL server hosts other databases — never touch them.
- Never enter real vendor credentials; connector tests use synthetic or upstream-derived fixtures (record provenance in `SOURCE.md`).
- Mutating HTTP requests must send `Origin` (same origin) and `X-ControlHome: 1`; no CORS policy, ever.

## Working agreements for subagents
- Stay inside the paths your task owns. Don't start long-running servers (the integrator runs the app on ports 5080/5173).
- TDD for domain/engine/connector logic. Run the build + relevant tests before reporting; report exact commands and results.
- Commit messages: Conventional Commits (`feat(api): …`, `fix(web): …`, `chore: …`, `docs: …`, `test: …`).
