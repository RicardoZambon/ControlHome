# Phase 0 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A green, deployable walking skeleton: solution + web workspace + design-system package, database + Identity auth with a
setup-code-gated first-run wizard, the app shell with login/users/profile in pt-BR and English, a Docker image + compose, and CI/CD.

**Architecture:** Modular monolith (ASP.NET Core 10 minimal APIs) that serves a Vue 3 SPA from the same origin. Strict project
reference graph (Domain ← Application ← Infrastructure ← Api). Frontend is an npm workspace: `@controlhome/ui` (the single source
of tokens + primitives), `@controlhome/api-client` (generated from the committed OpenAPI document), `apps/web`.

**Tech Stack:** .NET SDK 10.0.302, EF Core 10.0.12 (SQL Server 2025, compat 170), ASP.NET Core Identity, Serilog, OpenAPI 3.1 +
Scalar; Node 24.21.0, Vue 3.5.43, Vite 8.3.4, TypeScript ~6.0.3, Tailwind 4.3.3, shadcn-vue 2.8.2 + reka-ui 2.11.0, Pinia 4.0.3,
Vue Router 5.4.0, vue-i18n 11.4.14, Vitest 5.0.3, Playwright 1.64.0, @hey-api/openapi-ts 0.99.0.

**Spec:** `C:\Users\samur\.claude\plans\i-want-you-to-foamy-flamingo.md` (approved plan) and `docs/architecture.md`.

## Global Constraints
- Every project targets `net10.0`; `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` on (Directory.Build.props).
- Package versions only in `Directory.Packages.props` / `web/package-lock.json` (integrator edits them; implementers list needs).
- No MediatR, AutoMapper, FluentAssertions, Moq. Tests: xunit.v3 4.0.2 + Shouldly 4.3.0 + NSubstitute 6.2.0.
- Reference graph: Domain (no deps) ← Application ← Infrastructure ← Api. Tests may reference anything.
- DTOs are C# `record`s defined once in `ControlHome.Application`. Endpoints return `TypedResults`.
- Errors are RFC 7807 ProblemDetails with an extension `code` (stable, dotted, e.g. `auth.invalid_credentials`); the SPA maps
  `code` → i18n key `errors.<code>`.
- Every mutating request needs `Origin` == the app's own origin AND header `X-ControlHome: 1`. No CORS policy, ever.
- Colors exist only in `web/packages/ui/src/styles/tokens.css`. Every user-visible string goes through vue-i18n (en + pt-BR).
- SQL Server 2025 is required (`UseCompatibilityLevel(170)`). Nothing before `builder.Build()` opens a DB/network connection.
- Database names must match `^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$` for any CREATE/DROP done by tooling or tests.
- No secret values in any committed file (connection strings live in user-secrets / gitignored `.env`).
- Ports: API 5080 (dev), Vite 5173 (dev), container 8711 (`ASPNETCORE_HTTP_PORTS`).

## Review Focus
1. Setup race / replay: two concurrent `POST /api/setup/complete` or a second call after completion → exactly one admin; later
   calls get 409 `setup.already_completed` (test in Task 0.2-C).
2. Locked-out / disabled user keeps a valid cookie → must be rejected within 1 minute (security-stamp interval 1 min; test that a
   disabled user's next `/api/auth/me` after stamp refresh returns 401) (Task 0.2-D).
3. Cross-origin POST / missing Origin / missing `X-ControlHome` → 403 `request.cross_origin` (Task 0.2-B).
4. Database unreachable at startup → API answers 503 `db.unavailable` (not a crash), `/api/setup/status` still answers, and the
   app becomes ready when the DB appears (Task 0.2-A, 0.4-B).
5. Language/theme switch persists across reload and logout/login (Task 0.3-B, E2E `theme-i18n`).

---

## Milestone 0.1 — Skeleton and loop

Three parallel tracks after the integrator commits the root files (`.gitattributes`, `.gitignore`, `CLAUDE.md`, this plan,
`docs/STATUS.md`). Each track owns disjoint paths.

### Task 0.1-A: .NET solution skeleton (owns `/*.props`, `global.json`, `.editorconfig`, `.config/`, `ControlHome.slnx`, `src/`, `tests/`)

**Files:**
- Create: `global.json` → `{ "sdk": { "version": "10.0.302", "rollForward": "latestFeature" } }`
- Create: `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, LangVersion latest, TreatWarningsAsErrors, EnforceCodeStyleInBuild,
  AnalysisLevel latest, `<NoWarn>$(NoWarn);CS1591</NoWarn>`, `InvariantGlobalization=false`)
- Create: `Directory.Packages.props` (CPM; pins below)
- Create: `.editorconfig` (C#: file-scoped namespaces, `var` when apparent, `_camelCase` private fields, braces required;
  `[*.{ts,vue,js,json,css,yml,yaml,md}]` indent 2; `end_of_line = lf`)
- Create: `.config/dotnet-tools.json` with `dotnet-ef` 10.0.12
- Create: `ControlHome.slnx` with folders `/src/`, `/tests/`
- Create: `src/ControlHome.Domain/ControlHome.Domain.csproj` (no package refs)
- Create: `src/ControlHome.Application/ControlHome.Application.csproj` → Domain
- Create: `src/ControlHome.Infrastructure/ControlHome.Infrastructure.csproj` → Application
- Create: `src/ControlHome.Api/ControlHome.Api.csproj` (Web SDK) → Infrastructure; `Properties/launchSettings.json` profile `http`
  on `http://localhost:5080`, `ASPNETCORE_ENVIRONMENT=Development` (no secrets); `UserSecretsId` = `controlhome-api`
- Create: `src/ControlHome.Api/Program.cs` (top-level; `public partial class Program;` at the end)
- Create: `src/ControlHome.Api/Hosting/SpaHostingExtensions.cs`
- Create: `tests/ControlHome.Api.IntegrationTests/` (xunit.v3, Microsoft.AspNetCore.Mvc.Testing, Shouldly)
- Create: `tests/ControlHome.Architecture.Tests/` (xunit.v3, NetArchTest.Rules 1.3.2, Shouldly)

**Pins for Directory.Packages.props (0.1 needs only the first block; later milestones add the rest):**
```
Microsoft.AspNetCore.Mvc.Testing 10.0.12 · Microsoft.NET.Test.Sdk 18.10.1 · xunit.v3 4.0.2 · xunit.runner.visualstudio 4.0.1
Shouldly 4.3.0 · NSubstitute 6.2.0 · NetArchTest.Rules 1.3.2
--- 0.2+ ---
Microsoft.EntityFrameworkCore.SqlServer 10.0.12 · Microsoft.EntityFrameworkCore.Design 10.0.12 · Microsoft.EntityFrameworkCore 10.0.12
Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.12 · Microsoft.Extensions.Identity.Stores 10.0.12
Microsoft.AspNetCore.DataProtection.EntityFrameworkCore 10.0.12 · Microsoft.AspNetCore.OpenApi 10.0.12
Microsoft.Extensions.ApiDescription.Server 10.0.12 · Scalar.AspNetCore 2.17.14 · Serilog.AspNetCore 10.0.0 · Serilog.Sinks.File 7.0.0
FluentValidation 12.2.0 · FluentValidation.DependencyInjectionExtensions 12.2.0 · Microsoft.Extensions.TimeProvider.Testing 10.10.0
Testcontainers.MsSql 4.16.0
```

**Interfaces:**
- Produces: `GET /health/live` → 200 `text/plain` "Healthy" (anonymous). `GET /health/ready` → same in 0.1 (DB check added in 0.2).
- Produces: `app.UseControlHomeSpa()` — serves static files from `ControlHome:SpaPath` (absolute or relative to content root) if
  set, else `wwwroot`; `MapFallbackToFile("index.html")` for any non-`/api`, non-`/hubs`, non-`/health` path; unknown `/api/*`
  paths return 404 ProblemDetails (never index.html).

- [ ] **Step 1: Write failing tests** in `tests/ControlHome.Api.IntegrationTests/HealthTests.cs`:
  `Live_returns_200_Healthy` (GET `/health/live` → 200, body "Healthy"), `Unknown_api_path_returns_404_not_spa`
  (GET `/api/nope` → 404, content-type not text/html), `Spa_fallback_serves_index_html` (with a temp `SpaPath` containing
  `index.html` → GET `/floors/1` returns its contents). Use `WebApplicationFactory<Program>` with `UseSetting`.
- [ ] **Step 2:** `dotnet test` → FAIL (no Program).
- [ ] **Step 3:** Implement Program.cs + SpaHostingExtensions.
- [ ] **Step 4:** Write `tests/ControlHome.Architecture.Tests/ReferenceRulesTests.cs`: Domain has no dependency on
  Application/Infrastructure/Api/Microsoft.EntityFrameworkCore/Microsoft.AspNetCore; Application has no dependency on
  Infrastructure/Api/Microsoft.AspNetCore; Infrastructure has no dependency on Api. (Load assemblies via a marker type per project:
  add `public sealed class AssemblyMarker;` in each src project root namespace.)
- [ ] **Step 5:** `dotnet build -warnaserror` and `dotnet test` → all PASS.
- [ ] **Step 6:** Commit `feat(api): solution skeleton with health and SPA hosting`.

### Task 0.1-B: Web workspace + design-system package (owns `web/`)

**Files:**
- Create: `web/package.json` — `"private": true`, `"workspaces": ["packages/*", "apps/*"]`, `"engines": {"node": ">=24 <25"}`,
  scripts: `dev` (vite in apps/web), `build`, `lint` (eslint .), `typecheck` (vue-tsc -b in apps/web + packages/ui),
  `test` (vitest run --project all), `check:colors` (`node scripts/check-colors.mjs`), `format` (prettier --write).
- Create: `web/.nvmrc` = `24.21.0`, `web/.npmrc` = `engine-strict=true`, `web/eslint.config.js` (flat: typescript-eslint,
  eslint-plugin-vue flat/recommended, eslint-plugin-better-tailwindcss with `entryPoint: packages/ui/src/styles/index.css`,
  rules `no-unregistered-classes: error`, `no-unknown-classes: error`), `web/.prettierrc.json` (`semi: false, singleQuote: true,
  printWidth: 110`), `web/vitest.workspace.ts` (or `test.projects` in a root vitest.config.ts).
- Create: `web/scripts/check-colors.mjs` — scans `packages/*/src/**/*.{vue,ts,css}` and `apps/*/src/**/*.{vue,ts,css}`, skipping
  `packages/ui/src/styles/tokens.css`; fails (exit 1, prints file:line) on `#[0-9a-fA-F]{3,8}\b` inside style/attribute/CSS
  contexts, `rgb(a)?\(`, `hsl(a)?\(`, `oklch\(`, `oklab\(`, `color-mix\(` literals. Allowlist comment `/* color-ok */` on a line.
- Create: `web/packages/ui/package.json` — name `@controlhome/ui`, `"type": "module"`, `"exports"`: `"."` → `./src/index.ts`,
  `"./styles.css"` → `./src/styles/index.css`, `"./components/*"` → `./src/components/*`; peerDependencies `vue`, `reka-ui`.
- Create: `web/packages/ui/components.json` (shadcn-vue: `typescript: true`, `tailwind.css: src/styles/index.css`,
  `tailwind.baseColor: neutral`, aliases `components: @controlhome/ui/components`, `utils: @controlhome/ui/lib/utils`,
  `ui: @controlhome/ui/components/ui`), `web/packages/ui/tsconfig.json`.
- Create: `web/packages/ui/src/styles/tokens.css` — `:root` and `.dark` OKLCH custom properties. Required token names:
  `--background --foreground --card --card-foreground --popover --popover-foreground --primary --primary-foreground --secondary
  --secondary-foreground --muted --muted-foreground --accent --accent-foreground --destructive --destructive-foreground --border
  --input --ring --radius --sidebar --sidebar-foreground --sidebar-accent --sidebar-accent-foreground --sidebar-border` plus
  semantic device-state tokens `--state-on --state-off --state-offline --state-warn --state-energy --state-danger --state-info`
  (each with `-foreground`). Palette: calm slate/neutral surfaces, primary = teal/cyan (hue ≈ 195), energy = amber, danger = red.
- Create: `web/packages/ui/src/styles/theme.css` — `@theme inline { --color-*: initial; --color-background: var(--background); … }`
  mapping every token above (plus `--color-black`, `--color-white`, `--color-transparent`, `--color-current`) and `--radius-*`.
- Create: `web/packages/ui/src/styles/index.css` — `@import "tailwindcss"; @import "./tokens.css"; @import "./theme.css";
  @custom-variant dark (&:is(.dark *));` base layer (body bg/fg, border color).
- Create: `web/packages/ui/src/lib/utils.ts` — `export function cn(...inputs: ClassValue[]): string` (clsx + tailwind-merge).
- Create (via `npx shadcn-vue@2.8.2 add button card input` run inside `packages/ui`, then fixed to use only tokens):
  `src/components/ui/button/*`, `src/components/ui/card/*`, `src/components/ui/input/*`; `src/index.ts` re-exports them + `cn`.
- Create: `web/apps/web/` — `package.json` (name `@controlhome/web`), `index.html` (`<html lang="en">`, title ControlHome),
  `vite.config.ts` (plugins vue + `@tailwindcss/vite`; `server.port 5173`, `strictPort`, proxy `/api`, `/hubs` (ws: true),
  `/health` → `http://localhost:5080` with `changeOrigin: false`; `build.outDir: 'dist'`), `tsconfig.json`/`tsconfig.app.json`,
  `src/main.ts`, `src/App.vue` (placeholder: Card with app name + Button), `src/styles/main.css` (`@import "@controlhome/ui/styles.css";
  @source "../../../packages/ui/src";`), `src/env.d.ts`.
- Test: `web/packages/ui/src/lib/utils.test.ts` (`cn('px-2','px-4') === 'px-4'`), `web/apps/web/src/App.test.ts` (mounts App,
  finds text "ControlHome").

**Dependency pins:** vue 3.5.43, vite 8.3.4, @vitejs/plugin-vue 6.0.10, typescript ~6.0.3, vue-tsc 3.3.13, tailwindcss 4.3.3,
@tailwindcss/vite 4.3.3, reka-ui 2.11.0, class-variance-authority 0.7.1, clsx 2.1.1, tailwind-merge 3.7.0, lucide-vue-next 1.0.0,
vitest 5.0.3, @vue/test-utils 2.5.1, jsdom 30.1.2, eslint 10.12.0, typescript-eslint 8.71.1, eslint-plugin-vue 10.11.1,
@vue/eslint-config-typescript 14.9.0, eslint-plugin-better-tailwindcss 4.9.0, prettier 3.9.10, shadcn-vue 2.8.2 (devDependency).
If a pin fails to resolve with peers (ERESOLVE), pick the nearest compatible version and report it — never `--force`/`--legacy-peer-deps`.

- [ ] **Step 1:** Scaffold files; `npm install` in `web/` (Node 24 — verify `node -v` starts with v24).
- [ ] **Step 2:** Write the two failing tests; run `npm test` → FAIL.
- [ ] **Step 3:** Implement; run `npm test`, `npm run lint`, `npm run typecheck`, `npm run build`, `npm run check:colors` → all PASS.
- [ ] **Step 4:** Prove the color guard: temporarily add `class="text-red-500"` and a `#ff0000` style in App.vue → `lint`/`check:colors`
  fail; revert.
- [ ] **Step 5:** Commit `feat(web): workspace, design tokens and ui package`.

### Task 0.1-C: Repo tooling, CI, scripts, docs (owns `.github/`, `.githooks/`, `scripts/`, `.claude/launch.json`, `.gitleaks.toml`, `README.md`, `docs/` except this plan)

**Files:**
- Create: `.github/workflows/ci.yml` — `on: pull_request, push (branches: main)`; `permissions: contents: read`; concurrency per ref.
  Jobs (all `runs-on: ubuntu-latest`, `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1` with
  `persist-credentials: false`):
  - `backend`: `actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0` (`global-json-file: global.json`);
    `dotnet tool restore`; `dotnet restore`; `dotnet build --no-restore -warnaserror -c Release`;
    `dotnet test --no-build -c Release`.
  - `frontend`: `actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0` (`node-version-file: web/.nvmrc`,
    `cache: npm`, `cache-dependency-path: web/package-lock.json`); in `web/`: `npm ci`, `npm run lint`, `npm run typecheck`,
    `npm test`, `npm run check:colors`, `npm run build`.
  - `secrets`: `docker run --rm -v "$PWD:/repo" ghcr.io/gitleaks/gitleaks:v8.30.1 git /repo --config /repo/.gitleaks.toml
    --redact --verbose` (needs `fetch-depth: 0`).
- Create: `.gitleaks.toml` — `[extend] useDefault = true` + rules: `connection-string-password`
  (`(?i)(password|pwd)\s*=\s*[^;"'\s]{3,}`), `controlhome-dev-login` (`(?i)user\s*id\s*=\s*ControlHome\b`),
  `rtsp-credentials` (`rtsp://[^:/\s]+:[^@\s]+@`); allowlist paths `docs/plans/`, `**/*.md` for the regex examples only via
  `[[allowlists]] regexes` on the literal example strings.
- Create: `.githooks/pre-commit` (bash, `exec scripts/check-secrets.sh`), `scripts/check-secrets.sh`: (1) `git diff --cached -U0`
  → fail if it contains any value from `dotnet user-secrets list --project src/ControlHome.Api` (values ≥ 4 chars; values are never
  printed); (2) run gitleaks if on PATH (`gitleaks git --pre-commit --staged --config .gitleaks.toml`), else if `docker info`
  works run the pinned image on the staged diff, else print a warning. Exit non-zero on findings.
- Create: `scripts/setup-dev.ps1` + `scripts/setup-dev.sh`: check Node major 24 (print `nvm use 24.21.0` hint), `git config
  core.hooksPath .githooks`, `dotnet tool restore`, `dotnet restore`, `npm ci --prefix web`; optional `-ConnectionString` param →
  `dotnet user-secrets set ConnectionStrings:ControlHome <value> --project src/ControlHome.Api` (never echoed).
- Create: `scripts/dev.ps1` — starts API (`dotnet watch run --project src/ControlHome.Api --launch-profile http`) and Vite
  (`npm run dev --prefix web`) as background jobs, Ctrl+C stops both.
- Create: `scripts/reset-dev-db.ps1 -Name ControlHome_Dev` — validates the name regex, then `sqlcmd` DROP/CREATE using a
  connection read from user-secrets (server/login/password parsed, never printed); refuses anything else.
- Create: `.claude/launch.json` — `{"version":"0.0.1","configurations":[{"name":"api","runtimeExecutable":"dotnet","runtimeArgs":
  ["run","--project","src/ControlHome.Api","--launch-profile","http"],"port":5080},{"name":"web","runtimeExecutable":"npm",
  "runtimeArgs":["run","dev","--prefix","web"],"port":5173}]}`
- Create: `README.md` (what it is, feature roadmap summary, quickstart for dev + docker), `docs/development.md` (prereqs, setup-dev,
  user-secrets, ports, DB safety rules, running tests, E2E later), `docs/architecture.md` (copy the approved plan's Architecture
  section, cleaned up as living doc), `docs/roadmap.md` (milestone list with status checkboxes), `docs/adr/0001-stack-and-architecture.md`
  (context, decision, consequences: .NET 10 modular monolith, Vue+shadcn-vue+Tailwind 4 tokens, SQL 2025 required, no HA dep,
  connectors per vendor), `docs/adr/README.md` (index + template).

- [ ] **Step 1:** Write files. **Step 2:** `bash -n scripts/check-secrets.sh`; run `scripts/check-secrets.sh` on an empty staged
  set → exit 0; stage a temp file containing `Password=hunter2x;` → exit 1; unstage + delete.
- [ ] **Step 3:** Validate workflow YAML (`npx --yes yaml-lint`-free: use `python -c "import yaml,sys;yaml.safe_load(open(sys.argv[1]))"`
  or node `yaml` parse). **Step 4:** Commit `chore: ci, secret scanning, dev scripts and docs`.

### Task 0.1-D (integrator): merge, verify, PR
- [ ] Merge A, B, C; run `dotnet build -warnaserror && dotnet test` and the web suite; push `milestone/0.1-skeleton`; open PR;
  `gh pr checks --watch`; squash-merge; update `docs/STATUS.md`.

---

## Milestone 0.2 — Data and auth API

### Task 0.2-A: Persistence, database readiness, Data Protection (Infrastructure)

**Files:**
- Create: `src/ControlHome.Domain/Homes/Home.cs` — `Id` (int, always 1), `Name`, `TimeZoneId` (IANA), `Latitude` (double?),
  `Longitude` (double?), `Locale` ("pt-BR" | "en"), `Currency` ("BRL"), `Tariff` (complex: `Kind` = "flat",
  `PricePerKwh` decimal?), `SetupCompletedAtUtc` (DateTimeOffset?), `CreatedAtUtc`.
- Create: `src/ControlHome.Domain/Auditing/AuditLogEntry.cs` — `Id` long, `OccurredAtUtc`, `ActorType` (enum User|System|Anonymous),
  `ActorId` string?, `ActorName` string?, `Action` string (e.g. `auth.login.succeeded`), `TargetType` string?, `TargetId` string?,
  `DetailsJson` string? (json column), `IpAddress` string?.
- Create: `src/ControlHome.Domain/Security/Roles.cs` — `public static class Roles { Admin = "Admin"; Member = "Member"; Viewer = "Viewer"; All }`.
- Create: `src/ControlHome.Application/Identity/AppUser.cs` — `AppUser : IdentityUser<Guid>` + `DisplayName`, `Locale`, `Theme`
  ("light"|"dark"|"system"), `IsDisabled`, `CreatedAtUtc`, `LastLoginAtUtc`, `ConsecutiveLockouts` (int).
- Create: `src/ControlHome.Application/Abstractions/IControlHomeDb.cs` — `DbSet<Home> Homes`, `DbSet<AuditLogEntry> AuditLog`,
  `Task<int> SaveChangesAsync(CancellationToken)`.
- Create: `src/ControlHome.Infrastructure/Persistence/ControlHomeDbContext.cs` — `IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>,
  IControlHomeDb, IDataProtectionKeyContext`; schema `ch` for app tables, `auth` for Identity tables; configurations in
  `Persistence/Configurations/*.cs`.
- Create: `src/ControlHome.Infrastructure/Persistence/DatabaseNameGuard.cs` — `public static void EnsureAllowed(string databaseName)`
  throws `InvalidOperationException` unless it matches `^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$` OR equals the configured
  production name `ControlHome` *and* the caller is the app's own create-if-missing path (`allowProduction: true`).
- Create: `src/ControlHome.Infrastructure/Persistence/DatabaseReadiness.cs` — singleton; `DatabaseState` enum
  `NotConfigured | Connecting | Migrating | Ready | Failed`; `string? LastError`; `Task WaitUntilReadyAsync(CancellationToken)`.
- Create: `src/ControlHome.Infrastructure/Persistence/DatabaseInitializer.cs` — `BackgroundService`: resolve connection
  (`IDatabaseConnectionProvider`), retry connect with backoff up to `Database:StartupTimeout` (default 10 min); check
  `SERVERPROPERTY('ProductMajorVersion') >= 17` else `Failed("sql.version_unsupported")`; `MigrateAsync` when
  `Database:AutoMigrate` (default true); if the DB was created now → `ALTER DATABASE CURRENT SET RECOVERY SIMPLE` +
  `COMPATIBILITY_LEVEL = 170`; else verify compat ≥ 170 (fail with clear message); seed roles; state → Ready.
- Create: `src/ControlHome.Infrastructure/Persistence/IDatabaseConnectionProvider.cs` + `ConfigurationDatabaseConnectionProvider`
  (0.2: `ConnectionStrings:ControlHome`; 0.4 adds `database.json`). Returns `string?`.
- Create: `src/ControlHome.Infrastructure/Security/DataProtectionSetup.cs` — `AddControlHomeDataProtection(IServiceCollection,
  string dataPath)`: `SetApplicationName("ControlHome")`, `PersistKeysToDbContext<ControlHomeDbContext>()`,
  `ProtectKeysWithCertificate(cert)` + `UnprotectKeysWithAnyCertificate(cert)`; cert from `{dataPath}/keys/dataprotection.pfx`,
  generated on first use (RSA 3072, CN=ControlHome Data Protection, 100 years, exportable, no password; Unix mode 0600).
- Create: `src/ControlHome.Infrastructure/Auditing/AuditLog.cs` implements `IAuditLog` (Application: `Task WriteAsync(AuditEvent e,
  CancellationToken ct)`; `AuditEvent(string Action, string? TargetType = null, string? TargetId = null, object? Details = null)`;
  actor + IP from `ICurrentActor` (Application interface; Api implements from HttpContext)).
- Create: migration `Initial` (`dotnet ef migrations add Initial -p src/ControlHome.Infrastructure -s src/ControlHome.Api -o Persistence/Migrations`).
- Modify: `Program.cs` — `UseSqlServer(conn, o => o.UseCompatibilityLevel(170).EnableRetryOnFailure())`; `/health/ready` checks
  `DatabaseReadiness.State == Ready`; `DatabaseGateMiddleware`: when not Ready, `/api/*` (except `/api/setup/status`) → 503
  ProblemDetails `code=db.unavailable` with `state`.
- Test (`tests/ControlHome.Infrastructure.Tests/`): `DatabaseNameGuardTests` (allows `ControlHome_Dev`, `ControlHome_Test_ab12`,
  `ControlHome_Compose`; rejects `GeekVault`, `ControlHome_Dev;DROP`, `master`, `controlhome_dev`, `ControlHome_Prod`).
- Test (integration, real SQL): `DatabaseInitializerTests` — fresh DB → Ready, roles seeded, compat 170, recovery SIMPLE;
  unreachable server → state Connecting then Failed after short timeout; `/api/anything` → 503 `db.unavailable`.

**Test database fixture** (`tests/ControlHome.TestSupport/SqlServerFixture.cs`, shared by integration test projects):
if env `CONTROLHOME_TEST_SQLSERVER` (server-level connection string, no Initial Catalog) is set → create
`ControlHome_Test_<8 hex>` via `DatabaseNameGuard`, first dropping `ControlHome_Test_%` DBs older than 2 h; else start
Testcontainers `mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04` (`MSSQL_PID=Express`). Drop the DB on dispose.
Expose `string CreateDatabaseConnectionString(string suffix)`.

### Task 0.2-B: Request edge (Api/Security)

**Files:** `src/ControlHome.Api/Security/OriginValidationMiddleware.cs`, `NetworkAllowlistMiddleware.cs`,
`SecurityHeadersMiddleware.cs`, `RequestEdgeExtensions.cs` (`UseControlHomeRequestEdge()`), options `NetworkOptions`
(`AllowedClientNetworks` CIDR list, default `127.0.0.0/8, ::1/128, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10,
fc00::/7, fe80::/10`; `TrustedProxyNetworks` default empty).
- Origin rule: for methods other than GET/HEAD/OPTIONS **and** for any WebSocket upgrade request: `Origin` must equal
  `{Request.Scheme}://{Request.Host}` (after forwarded headers) → else 403 ProblemDetails `request.cross_origin`. For non-WebSocket
  mutations also require `X-ControlHome: 1` → else 403 `request.missing_header`. `/api/hooks/*` is exempt (later milestone).
- Network rule: `RemoteIpAddress` null (TestServer) is allowed; IPv4-mapped IPv6 normalized; outside allowlist → 403
  `request.network_denied` + audit `security.network_denied`.
- Headers on every response: CSP `default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self';
  style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'`
  (path `/scalar*` in Development gets Scalar's needs), `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`,
  `X-Frame-Options: DENY`.
- Tests (`tests/ControlHome.Api.IntegrationTests/RequestEdgeTests.cs`): POST without Origin → 403 `request.cross_origin`;
  POST with `Origin: http://evil.local` → 403; POST same-origin without `X-ControlHome` → 403 `request.missing_header`;
  same-origin + header → passes to endpoint; GET without Origin → OK; WebSocket-upgrade GET with foreign Origin → 403;
  response has CSP + nosniff; request from 8.8.8.8 (set via a test `IStartupFilter` that sets RemoteIpAddress) → 403.

### Task 0.2-C: Setup lifecycle + setup code

**Files:** Application `Setup/SetupService.cs`, `Setup/SetupContracts.cs`; Infrastructure `Setup/SetupCodeProvider.cs`
(`ISetupCodeProvider`: `string Current`, `bool Verify(string candidate)`, `void Rotate()`); Api `Features/Setup/SetupEndpoints.cs`.
- Contracts: `SetupStatusDto(bool SetupCompleted, string DatabaseState, bool DatabaseConfigured, string Version)`;
  `CompleteSetupRequest(string SetupCode, AdminAccountInput Admin, HomeInput Home)`;
  `AdminAccountInput(string UserName, string DisplayName, string Password)`;
  `HomeInput(string Name, string TimeZoneId, double? Latitude, double? Longitude, string Locale, string Currency, decimal? PricePerKwh)`.
- Endpoints (anonymous): `GET /api/setup/status` → `SetupStatusDto` (works when DB not ready: `SetupCompleted=false`);
  `POST /api/setup/complete` → 200 `MeDto` and signs the admin in. Rate limit policy `setup`: 5 / 10 min per IP.
- Rules: code normalized (upper, remove `-`/spaces), compared with `CryptographicOperations.FixedTimeEquals`; code = 12 Crockford
  base32 chars from `RandomNumberGenerator`, displayed as `XXXX-XXXX-XXXX`; `Setup:Code` config overrides; logged at Warning
  (`"First-run setup code: {SetupCode}"`) and written to `{dataPath}/setup-code.txt` only while setup is incomplete; deleted on
  completion; 20 failed verifications → `Rotate()` + log new code. Completion runs in a transaction holding
  `sp_getapplock('ControlHome.Setup', 'Exclusive', 'Transaction')`; if `SetupCompletedAtUtc` already set → 409
  `setup.already_completed`. Creates roles if missing, the admin (role Admin), the Home row, sets `SetupCompletedAtUtc`, audits
  `setup.completed`. Validation (FluentValidation): user name 3–64 `[A-Za-z0-9._@-]`, password ≥ 10 chars, home name 1–80,
  TimeZoneId must resolve via `TimeZoneInfo.TryFindSystemTimeZoneById`, Locale ∈ {pt-BR, en}, Currency 3 letters, lat −90..90,
  lon −180..180. Wrong code → 400 `setup.invalid_code`.
- Tests: status before/after; wrong code → 400 and no user created; valid → 200, cookie set, `/api/auth/me` works; second call →
  409; **two concurrent completes → exactly one 200**; `Setup__Code` override honoured; validation errors → 400 with `errors`.

### Task 0.2-D: Auth, profile, users, home, audit endpoints

**Files:** Api `Features/Auth/AuthEndpoints.cs`, `Features/Users/UserEndpoints.cs`, `Features/Home/HomeEndpoints.cs`,
`Features/Audit/AuditEndpoints.cs`, `Hosting/IdentitySetup.cs`, `Hosting/CurrentActor.cs`; Application `Users/UserAdminService.cs`,
`Users/UserContracts.cs`, `Auth/AuthContracts.cs`, `Homes/HomeService.cs`, `Homes/HomeContracts.cs`.
- Identity: password `RequiredLength=10`, no digit/upper/lower/non-alnum requirements; `User.RequireUniqueEmail=false`;
  lockout `MaxFailedAccessAttempts=5`, `DefaultLockoutTimeSpan=5 min`, escalation on repeated lockouts: `5 min × 2^(ConsecutiveLockouts)`
  capped at 1 h (reset on success); `SecurityStampValidatorOptions.ValidationInterval = 1 min`. Cookie: name
  `ControlHome.Session`, HttpOnly, `SameSite=Strict`, `SecurePolicy=SameAsRequest`, 30-day sliding; `OnRedirectToLogin` → 401,
  `OnRedirectToAccessDenied` → 403 (ProblemDetails). Authorization: fallback policy = authenticated; policies `Admin`
  (role Admin), `Member` (Admin|Member), `Viewer` (any role). Disabled users fail login (`auth.disabled`) and their stamp is
  updated on disable.
- Contracts: `MeDto(Guid Id, string UserName, string DisplayName, string[] Roles, string Locale, string Theme)`;
  `LoginRequest(string UserName, string Password, bool RememberMe)`; `UpdateProfileRequest(string DisplayName, string Locale,
  string Theme)`; `ChangePasswordRequest(string CurrentPassword, string NewPassword)`; `UserDto(Guid Id, string UserName,
  string DisplayName, string Role, bool IsDisabled, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastLoginAtUtc)`;
  `CreateUserRequest(string UserName, string DisplayName, string Password, string Role)`; `UpdateUserRequest(string DisplayName,
  string Role, bool IsDisabled)`; `ResetPasswordRequest(string NewPassword)`; `HomeDto` (all Home fields + `PricePerKwh`);
  `UpdateHomeRequest` (same minus Id/Setup fields); `AuditEntryDto`; `PagedResult<T>(IReadOnlyList<T> Items, long? NextCursor)`.
- Endpoints: `POST /api/auth/login` (anon, rate limit `login` 10/min/IP) → 200 `MeDto` | 401 `auth.invalid_credentials` |
  423 `auth.locked_out` (with `retryAfterSeconds`) | 403 `auth.disabled`; `POST /api/auth/logout` → 204; `GET /api/auth/me` → `MeDto`
  | 401; `PUT /api/auth/me/profile` → `MeDto`; `POST /api/auth/me/password` → 204 | 400 `auth.password_incorrect`;
  `GET /api/users` (Admin) → `UserDto[]`; `POST /api/users` (Admin) → 201; `PUT /api/users/{id}` (Admin); `POST /api/users/{id}/password`
  (Admin) → 204; `DELETE /api/users/{id}` (Admin) → 204; last-active-Admin protection → 409 `users.last_admin` on delete,
  demote or disable; an admin can't delete themselves (409 `users.cannot_delete_self`). `GET /api/home` (Viewer) → `HomeDto`;
  `PUT /api/home` (Admin). `GET /api/audit?cursor&take=50` (Admin) → `PagedResult<AuditEntryDto>` newest first.
- Audit actions: `auth.login.succeeded|failed|locked_out`, `auth.logout`, `auth.password.changed`, `users.created|updated|deleted|
  password_reset`, `home.updated`, `setup.completed`.
- CLI: `dotnet ControlHome.Api.dll reset-admin <userName>` → prints a one-time 15-minute reset token (Identity
  `GeneratePasswordResetTokenAsync`) and the URL path `/reset-password?user=..&token=..`; `POST /api/auth/reset-password`
  (anon, rate-limited) consumes it. Audit `auth.admin_reset`.
- Tests: login ok/bad/locked (5 failures → 423)/disabled; me 401 when anonymous; profile update persists; change password; users
  CRUD as Admin; Member gets 403 on `/api/users`; last-admin guard (delete/demote/disable); disabled user's existing cookie → 401
  after stamp revalidation (advance via `FakeTimeProvider` or set interval to zero in test); audit entries written.

### Task 0.2-E: OpenAPI, client generation, logging, ProblemDetails, test endpoints

- OpenAPI: `builder.Services.AddOpenApi()` with a document transformer adding the `X-ControlHome` header note and ProblemDetails
  schema with `code`; `MapOpenApi()` + `MapScalarApiReference()` only in Development. Build-time document:
  `<OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)/../../web/packages/api-client</OpenApiDocumentsDirectory>`,
  `<OpenApiGenerateDocumentsOptions>--file-name openapi</OpenApiGenerateDocumentsOptions>` → `web/packages/api-client/openapi.json`
  (committed).
- `web/packages/api-client`: `package.json` (`@controlhome/api-client`), `openapi-ts.config.ts` (input `openapi.json`, output
  `src/generated`, plugins `@hey-api/client-fetch`, `@hey-api/typescript`, `@hey-api/sdk`), script `gen` ; `src/index.ts` exports
  generated SDK + `configureApiClient({ onUnauthorized })` which sets `baseUrl: ''`, `credentials: 'same-origin'`, header
  `X-ControlHome: 1`. Root `web/package.json` script `gen:api`.
- ProblemDetails: `AddProblemDetails` customizing `code`, `traceId`; exception handler; validation errors → 400 `validation.failed`
  with `errors` dictionary (FluentValidation endpoint filter `ValidationFilter<T>`).
- Serilog: `UseSerilog` reading `Serilog` config; console (Development: text; otherwise compact text with timestamps) + file
  `{dataPath}/logs/controlhome-.log` rolling daily, 14 files; `UseSerilogRequestLogging` with route template message;
  `System.Net.Http.HttpClient` minimum level Warning; `EnableSensitiveDataLogging` only in Development.
- Data path: `ControlHome:DataPath` (default `/data`; `appsettings.Development.json` → `../../.data`).
- Testing endpoints (`ASPNETCORE_ENVIRONMENT=Testing` only): `POST /api/_test/reset` (delete all rows from app + identity tables,
  rotate setup code back to configured value). Test: endpoint returns 404 in Production environment.
- CI: add job step `api-client drift`: `dotnet build` then `npm run gen:api` in `web/` then `git diff --exit-code web/packages/api-client`.

---

## Milestone 0.3 — Shell and auth UI

### Task 0.3-A: ui primitives + theme
- Add via shadcn-vue into `packages/ui`: label, select, switch, checkbox, dialog, alert-dialog, dropdown-menu, avatar, badge,
  separator, sheet, tooltip, sonner (toasts), table, tabs, skeleton, alert, form (vee-validate + zod), sidebar. Normalize all to
  tokens; export from `src/index.ts`.
- `src/composables/useTheme.ts`: `type ThemeMode = 'light'|'dark'|'system'`; `useTheme(): { mode: Ref<ThemeMode>, resolved:
  ComputedRef<'light'|'dark'>, setMode(m) }` — toggles `.dark` on `<html>`, persists `localStorage['ch.theme']`, follows
  `prefers-color-scheme` in system mode. Test with vitest (matchMedia mock).
- `src/composables/useThemeTokens.ts`: `useThemeTokens(): Ref<Record<string,string>>` (reads computed CSS vars, updates on theme
  change) — for charts later.

### Task 0.3-B: App foundation (apps/web)
- `src/i18n/index.ts`: `createAppI18n()` — `legacy: false`, locales `en`, `pt-BR`; messages merged from
  `import.meta.glob('../features/**/locales/*.json', { eager: true })` + `src/locales/{en,pt-BR}.json`; `missingWarn` → in test
  mode `missing` handler throws; locale persisted `localStorage['ch.locale']`, synced with `MeDto.locale`; sets `<html lang>`.
- `src/router/index.ts`: routes from `import.meta.glob('../features/*/routes.ts', { eager: true })` (each exports `routes:
  RouteRecordRaw[]`), meta `{ public?: boolean, roles?: string[], layout?: 'shell'|'bare', titleKey: string }`; guard: load
  setup status once → if `!setupCompleted` and route ≠ `/setup` → `/setup`; if setup done and route = `/setup` → `/`; non-public
  routes require session (load `/api/auth/me`) else `/login?redirect=`; role check → `/forbidden`.
- `src/stores/session.ts` (Pinia): `me: MeDto | null`, `status: SetupStatusDto | null`, actions `loadStatus()`, `loadMe()`,
  `login(req)`, `logout()`, `updateProfile(req)`; `hasRole(...roles)`.
- `src/lib/api.ts`: configures the client; 401 → clear session + redirect to login (except on `/api/auth/me` probe).
  `src/lib/problem.ts`: `toErrorMessage(problem, t)` → `t('errors.' + code)` fallback `errors.unknown`.
- Layout `src/components/layout/AppShell.vue` (sidebar via ui `Sidebar`, top bar: page title, theme toggle, language select, user
  menu with Profile/Logout; mobile sheet), nav items registered via feature `nav.ts` exports (`{ to, icon, labelKey, roles?, order }`).
- Features: `features/setup` (wizard: Code → Admin → Home (time zone select from `Intl.supportedValuesOf('timeZone')`, default
  `America/Sao_Paulo`; "use my location" via geolocation; language; currency; price per kWh) → Review/Finish),
  `features/auth` (Login, ResetPassword), `features/dashboard` (empty state placeholder), `features/users` (table + create/edit
  dialog, reset password, delete with confirm; errors via problem codes), `features/profile` (display name, language, theme,
  change password), `features/system` (NotFound, Forbidden, `/dev/ui-kit` for Admin: every token swatch + every primitive).
- Vitest: session store (login/logout/role), router guard logic (pure function `resolveNavigation(status, me, to)` unit-tested),
  i18n missing-key throws, `toErrorMessage`.

### Task 0.3-C: Playwright E2E + screenshots
- `web/tests/e2e/playwright.config.ts`: `baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:5080'`; projects `desktop`
  (1440×900) and `mobile` (375×812); `fixtures.ts` with global gates (fail on console error; `@axe-core/playwright` no serious/
  critical; no horizontal overflow at 375); helper `resetApp(request)` → `POST /api/_test/reset`; setup code from
  `E2E_SETUP_CODE` (default `E2E-SETUP-CODE-1`); test users in `fixtures/users.json` (non-secret).
- Specs: `setup.spec.ts` (redirect to /setup, wrong code error, complete wizard, lands on dashboard), `login.spec.ts`
  (logout, bad password message, login, redirect param), `users-admin.spec.ts` (create Member, Member can't see Users nav,
  last-admin guard message), `theme-i18n.spec.ts` (toggle dark persists across reload, switch to pt-BR persists across
  logout/login).
- `screenshots.spec.ts` (project `screenshots`, run by `npm run screenshots`): for routes `/setup`, `/login`, `/`, `/users`,
  `/profile`, `/dev/ui-kit` × {1440, 375} × {light, dark} × {en, pt-BR} → `web/tests/e2e/screenshots/<route>-<w>-<theme>-<locale>.png`.
- `scripts/e2e.ps1`: builds web, creates `ControlHome_Test_e2e` (guard), runs API with `ASPNETCORE_ENVIRONMENT=Testing`,
  `ControlHome__SpaPath=../../web/apps/web/dist`, `Setup__Code=E2E-SETUP-CODE-1`, waits for `/health/ready`, runs Playwright,
  stops API, drops DB.

---

## Milestone 0.4 — Packaging

### Task 0.4-A: Dockerfile + healthcheck
- `deploy/Dockerfile`: `ARG` digests; stage `web` = `node:24.21.0-alpine@sha256:…` `--platform=$BUILDPLATFORM` → `npm ci` + `npm run
  build`; stage `build` = `mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:…` `--platform=$BUILDPLATFORM` → `dotnet restore -a
  $TARGETARCH`, `dotnet publish src/ControlHome.Api -c Release -a $TARGETARCH --no-restore -o /out -p:OpenApiGenerateDocuments=false`;
  final = `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:…`: copy `/out` → `/app`, web `dist` → `/app/wwwroot`;
  `ENV ASPNETCORE_HTTP_PORTS=8711 ControlHome__DataPath=/data DOTNET_RUNNING_IN_CONTAINER=true`; `EXPOSE 8711`;
  `HEALTHCHECK CMD ["dotnet", "/app/ControlHome.Api.dll", "--healthcheck"]`; `ENTRYPOINT ["dotnet", "/app/ControlHome.Api.dll"]`;
  labels `org.opencontainers.image.source=https://github.com/RicardoZambon/ControlHome`, licenses Apache-2.0.
- `Program.cs`: if `args` contains `--healthcheck` → GET `http://127.0.0.1:{port}/health/live` (2 s timeout) → exit 0/1 before
  building the host. `.dockerignore` (bin, obj, node_modules, dist, .data, .git, tests/e2e/screenshots).
- Verify: `docker build -f deploy/Dockerfile -t controlhome:local .` and `docker run --rm controlhome:local --healthcheck` exits 1
  (nothing listening) quickly.

### Task 0.4-B: Pre-DB setup mode
- `IDatabaseConnectionProvider` gains `/data/config/database.json` (written by `POST /api/setup/database`), encrypted with a
  bootstrap Data Protection provider (`DataProtectionProvider.Create(new DirectoryInfo({dataPath}/keys/bootstrap), cfg =>
  cfg.ProtectKeysWithCertificate(cert))`).
- `POST /api/setup/database` (anon, setup code, rate limit `setup`): `DatabaseSetupRequest(string SetupCode, string Host, int Port,
  string Database, string UserName, string Password, bool TrustServerCertificate)` → tests connection (`SELECT
  SERVERPROPERTY('ProductMajorVersion')`) → 400 `db.connection_failed` (message sanitized) | 400 `sql.version_unsupported` | if DB
  missing and login can't create → 409 `db.create_denied` with `script` (T-SQL for DB + `ControlHome_App` login/user) → saves
  config → signals `DatabaseInitializer` to start → 202. Only allowed while no connection is configured.
- SPA: setup wizard Step 0 "Database" shown when `status.databaseConfigured === false`; polls status until `databaseState == Ready`.
- Tests: integration (no connection string → status NotConfigured; post with real test server → Ready); E2E `db-setup.spec.ts`
  (compose without connection string).
- As built: the provider exposes a change token that wakes the `DatabaseInitializer` (it waits in `NotConfigured` instead of
  ending); after the start-up timeout the state stays `Failed` and the initialization is retried every 60 s
  (`Database:RetryInterval`). `SetupStatusDto` gains `DatabaseSource` (`environment` | `file` | `none`) and `DatabaseError`.
  The app's create-if-missing path creates whatever name the operator configured (never drops); `DatabaseNameGuard` is for
  tooling and tests only. Wizard order: Code → Database (only if needed) → Admin → Home → Review; the setup code is typed once.
  `scripts/e2e.ps1 -PreDb` runs `db-setup.spec.ts` against a container without a connection string (E2E_DB_* env only).

### Task 0.4-C: Compose files + CI e2e + release + CodeQL + Dependabot
- `deploy/docker-compose.yml` (prod): service `controlhome` — `image: ghcr.io/ricardozambon/controlhome:${CONTROLHOME_VERSION:-edge}`,
  `network_mode: host`, `user: "${CONTROLHOME_UID:-1000}:${CONTROLHOME_GID:-1000}"`, env `ASPNETCORE_HTTP_PORTS=${CONTROLHOME_PORT:-8711}`,
  `ConnectionStrings__ControlHome=${CONTROLHOME_DB_CONNECTION:-}`, `Setup__Code=${CONTROLHOME_SETUP_CODE:-}`, `TZ`;
  `volumes: ./data:/data`; `read_only: true`; `tmpfs: [/tmp]`; `cap_drop: [ALL]`; `security_opt: [no-new-privileges:true]`;
  `restart: unless-stopped`. Profile `bundled-db`: service `mssql` — `mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04`,
  `MSSQL_PID=Express`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD=${MSSQL_SA_PASSWORD:?}`, `ports: ["127.0.0.1:1433:1433"]`,
  volume `mssql-data:/var/opt/mssql`, `mem_limit: 2g`, healthcheck via `/opt/mssql-tools18/bin/sqlcmd -C -Q "SELECT 1"`.
- `deploy/docker-compose.dev.yml` (override): `controlhome` → `build: {context: .., dockerfile: deploy/Dockerfile}`,
  `network_mode: bridge`, `ports: ["8080:8711"]`, `user: "1654:1654"`, connection string to `Server=mssql,1433;Database=ControlHome_Compose;…`
  from `.env`; e2e override `deploy/docker-compose.e2e.yml` sets `ASPNETCORE_ENVIRONMENT=Testing`, `Setup__Code=E2E-SETUP-CODE-1`.
- `deploy/.env.example` documenting every variable (placeholders only).
- `ci.yml` job `e2e` (needs backend+frontend): create `.env` from CI-generated SA password, `docker compose … up -d --build
  controlhome` first, then after 20 s `up -d mssql` (late DB), wait `/health/ready` (≤ 5 min), `npx playwright install --with-deps
  chromium`, run specs, then `npm run screenshots`, upload `playwright-report` + screenshots as artifacts; then a second run for
  `db-setup.spec.ts` with no connection string.
- `.github/workflows/release.yml`: on `push: tags: ['v*']` and `push: branches: [main]`; permissions `contents: read, packages: write,
  id-token: write, attestations: write`; buildx (no QEMU — Dockerfile cross-compiles), `docker/metadata-action` tags
  (`edge` on main, semver on tags, `sha`), `docker/build-push-action` platforms `linux/amd64,linux/arm64`, `sbom: true`,
  `provenance: mode=max`; `actions/attest-build-provenance`; final step `docker logout ghcr.io && docker pull …` (continue-on-error,
  writes a warning).
- `.github/workflows/codeql.yml`: `csharp` (build-mode `none`) + `javascript-typescript`; weekly + PR; `security-events: write`.
- `.github/dependabot.yml`: ecosystems `nuget` (/), `npm` (/web), `github-actions` (/), `docker` (/deploy); weekly;
  `cooldown: default-days: 7`; groups `minor-and-patch` per ecosystem; `open-pull-requests-limit: 2`.
- Exit: CI green incl. `e2e`; tag `v0.0.1`; release workflow publishes `ghcr.io/ricardozambon/controlhome:0.0.1` (multi-arch).
