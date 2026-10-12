# Developing ControlHome

Everything you need to build, run and test ControlHome locally. For the design see
[`architecture.md`](architecture.md); for where the project stands see [`STATUS.md`](STATUS.md) and
[`roadmap.md`](roadmap.md). Contributor and agent rules are summarized in [`CLAUDE.md`](../CLAUDE.md).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0.302** (pinned in `global.json`, `rollForward: latestFeature`) | `dotnet --version` |
| Node.js | **24.21.0** (`web/.nvmrc`; `web/.npmrc` has `engine-strict=true`) | via nvm: `nvm install 24.21.0 && nvm use 24.21.0` |
| Docker | Docker Desktop / Engine with Linux containers | integration tests (Testcontainers), compose, image builds, gitleaks |
| Git | any recent | Git Bash is enough on Windows for the hook |
| PowerShell | 7.2+ (`pwsh`) | `scripts/*.ps1` (Windows, macOS, Linux) |
| SQL Server 2025 | any edition, compatibility level 170 | for running the app; tests bring their own container |
| sqlcmd | optional | only for `scripts/reset-dev-db.ps1` |

> **Windows + nvm-windows:** if `nvm use` needs elevation, the scripts fall back to prepending
> `%LOCALAPPDATA%\nvm\v24.21.0` (or `%NVM_HOME%\v24.21.0`) to `PATH` for their own session. Don't install a second
> Node with winget.

## First-time setup

```powershell
git clone https://github.com/RicardoZambon/ControlHome.git
cd ControlHome
./scripts/setup-dev.ps1 -PromptConnectionString     # PowerShell 7 (Windows/macOS/Linux)
```

```bash
scripts/setup-dev.sh --prompt-connection-string      # bash alternative (Linux/macOS/Git Bash)
```

The setup script is idempotent. It:

1. checks for Node 24 (switching to nvm's 24.21.0 when installed);
2. enables the secret-scanning pre-commit hook — `git config core.hooksPath .githooks`;
3. runs `dotnet tool restore`, `dotnet restore` and `npm ci --prefix web`;
4. optionally stores your development connection string in user-secrets (see below). The value is read with hidden
   input, passed to `dotnet user-secrets` on stdin (never on a command line) and never echoed.

Omit `-PromptConnectionString` / `--prompt-connection-string` if you don't have a database yet.

## Database and user-secrets

The API reads `ConnectionStrings:ControlHome`. For development it lives **only in user-secrets** of
`src/ControlHome.Api` (`UserSecretsId` = `controlhome-api`) — never in `appsettings*.json`, never in the repo.

```powershell
./scripts/setup-dev.ps1 -PromptConnectionString
# or, by hand (the value then lands in your shell history):
dotnet user-secrets set ConnectionStrings:ControlHome "Server=<host>,1433;Database=ControlHome_Dev;User ID=<login>;Password=<password>;TrustServerCertificate=True" --project src/ControlHome.Api
dotnet user-secrets list --project src/ControlHome.Api   # prints secrets: don't paste the output anywhere
```

**Database safety rules** (the SQL server also hosts other databases):

- Tooling and tests may only create or drop databases whose name matches
  `^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$` (case-sensitive): `ControlHome_Dev`, `ControlHome_Dev_<topic>`,
  `ControlHome_Test_<id>`, `ControlHome_Compose`. Never touch any other database.
- The app requires **SQL Server 2025** (version ≥ 17, compatibility level 170). A database the app creates gets
  `RECOVERY SIMPLE` and compatibility level 170.
- Never run EF commands without an explicit `--connection` to a `ControlHome_Dev*` database. Migrations are created
  only by the integrator:
  `dotnet ef migrations add <Name> -p src/ControlHome.Infrastructure -s src/ControlHome.Api -o Persistence/Migrations`.
- To start over: `./scripts/reset-dev-db.ps1 -Name ControlHome_Dev` drops and re-creates the database (it refuses any
  other name, asks for confirmation, supports `-WhatIf`, and reads server/login/password from user-secrets without
  printing them). The app migrates it on the next start.

Until a database is reachable the API still starts: `/health/live` is OK, `/health/ready` is not, and `/api/*`
answers `503 db.unavailable` (from milestone 0.2).

## Running the app

| What | URL / port |
|---|---|
| API (Kestrel, `http` launch profile) | <http://localhost:5080> |
| Web dev server (Vite, proxies `/api`, `/hubs`, `/health` to 5080) | <http://localhost:5173> ← open this |
| Container (`ASPNETCORE_HTTP_PORTS`) | 8711 (dev compose maps it to 8080) |

Pick one way to run both processes:

- **Script:** `./scripts/dev.ps1` starts `dotnet watch run --project src/ControlHome.Api --launch-profile http` and
  `npm run dev --prefix web` in the same console. Ctrl+C (or either process exiting) stops both, including child
  processes. `-ApiOnly` / `-WebOnly` start just one.
- **Two terminals:** run those two commands yourself.
- **Editor / Claude Code preview:** `.claude/launch.json` defines `api` (5080) and `web` (5173).

Always use <http://localhost:5173> during development: requests then stay same-origin, so the request-edge rules
(mutations need `Origin` = the app's origin and `X-ControlHome: 1`; no CORS, ever) behave exactly like production.

From milestone 0.2, runtime data (Data Protection keys, logs, the first-run setup code) goes to
`ControlHome:DataPath`; in Development that is `.data/` at the repository root (gitignored). Until setup is completed,
the setup code is printed in the API log and written to `.data/setup-code.txt`; set `Setup__Code` to pin it.

**Don't run two copies at once** — e.g. the dev servers and the dev compose stack. Ports are fixed (Vite uses
`strictPort`), and only one runtime per database may be active.

## Building and testing

### Backend

```bash
dotnet build -warnaserror      # whole solution (ControlHome.slnx); warnings are errors
dotnet test                    # unit + architecture + integration
```

- Integration tests that need SQL Server start **Testcontainers** `mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04`
  (Docker must be running; the container starts on first use, so unit-only runs don't need it). Alternatively set
  `CONTROLHOME_TEST_SQLSERVER` to a *server-level* connection string (no `Initial Catalog`): the fixture then creates
  `ControlHome_Test_<8 hex run id><suffix>` databases, drops them afterwards and first cleans up `ControlHome_Test_%`
  databases older than 2 h.
- Shared test infrastructure lives in `tests/ControlHome.TestSupport`: `SqlServerFixture` (xunit assembly fixture),
  `ControlHomeApiFactory` (environment `Testing`, its own database and temp data path, setup code
  `TEST-SETUP-0001-CODE`, waits for `/health/ready`) and `CreateApiClient()` (same-origin `Origin`, `X-ControlHome: 1`,
  cookies).
- `tests/ControlHome.Architecture.Tests` enforces the project reference graph
  (`Domain` ← `Application` ← `Infrastructure` ← `Api`).
- **Windows Smart App Control** may block freshly built `ControlHome.*.dll` files ("An Application Control policy has
  blocked this file"). Don't change the Windows setting; run the tests in Linux with `./scripts/test-docker.ps1`. The
  build-time OpenAPI step also loads the built DLLs; if it is blocked, build with
  `dotnet build -p:OpenApiGenerateDocumentsOnBuild=false` and let CI (or a Linux build) regenerate `openapi.json`.
- Rules of the house: xunit.v3 + Shouldly + NSubstitute; no MediatR / AutoMapper / FluentAssertions / Moq; inject
  `TimeProvider`; TDD for domain, engine and connector logic.

### Frontend (`web/`)

```bash
cd web
npm ci
npm run lint && npm run typecheck && npm test && npm run check:colors && npm run build
npm run dev           # Vite on 5173 (same as `npm run dev --prefix web` from the root)
npm run gen:api       # regenerate @controlhome/api-client from packages/api-client/openapi.json (from 0.2)
```

- `npm run check:colors` fails on any hex/rgb/hsl/oklch/color-mix literal outside
  `packages/ui/src/styles/tokens.css`. Use the semantic Tailwind classes (`bg-primary`, `text-muted-foreground`,
  `bg-state-on`, …); Tailwind's default palette is disabled.
- Every user-visible string goes through vue-i18n with keys in **both** `en` and `pt-BR`; missing keys throw in tests.
- `dotnet build` of `ControlHome.Api` regenerates `web/packages/api-client/openapi.json`; commit it together with the
  regenerated client (CI fails on drift from 0.2).

### End-to-end tests (from milestone 0.3)

```powershell
cd web; npx playwright install chromium; cd ..   # once
./scripts/e2e.ps1                                # the specs (desktop 1440×900 and mobile 375×812 projects)
./scripts/e2e.ps1 -Screenshots                   # npm run screenshots instead
./scripts/e2e.ps1 -NoBuild --project desktop --grep "last admin"   # reuse the image; extra args go to Playwright
```

`scripts/e2e.ps1` builds the image from `deploy/Dockerfile` (`controlhome:e2e`; the API runs in Docker, so Smart App
Control is not involved), points it at the dev SQL Server from user-secrets with the database `ControlHome_Test_E2e`
(the connection string goes through a temporary `--env-file`, never the command line or the console), runs it with
`ASPNETCORE_ENVIRONMENT=Testing` (enables `/api/_test/*`), `Setup__Code=E2E-SETUP-CODE-1`, raised rate limits and a
tmpfs `/data` on <http://localhost:18711>, waits for `/health/ready`, runs Playwright, then removes the container and
drops the database (guarded name, via `sqlcmd`). Against an app you started yourself:
`E2E_BASE_URL=http://localhost:8080 npm run e2e` (CI uses the compose files plus `deploy/docker-compose.e2e.yml`).

Every test starts with `POST /api/_test/reset`, and every page passes the global gates in `web/tests/e2e/fixtures.ts`:
no console errors or uncaught exceptions (4xx answers from `/api/*` are part of the flows), no missing i18n keys (the
app warns `[i18n] Missing key …`), no axe-core violation of impact serious or critical, and no horizontal scrolling at
375 px (`gates.spec.ts` shows each gate failing a page that breaks it). `npm run screenshots` captures `/setup`, `/login`, `/`, `/users`, `/profile` and `/dev/ui-kit` at 1440/375 px
× light/dark × en/pt-BR into `web/tests/e2e/screenshots/` (gitignored), running the same gates on each. Reports:
`web/tests/e2e/playwright-report/{e2e,screenshots}` (`npx playwright show-report <folder>`).

## Secret scanning

This is a **public repository** for a real home. The pre-commit hook (`.githooks/pre-commit` →
`scripts/check-secrets.sh`) looks only at lines you are adding and blocks the commit when:

1. a value stored in your user-secrets (or the password inside a stored connection string) appears in them — the
   value is never printed, only the file name; or
2. **gitleaks** finds something (default rules plus `.gitleaks.toml`: passwords in connection strings / env files, the
   dev SQL login name, credentials in `rtsp://` URLs). It uses `gitleaks` from `PATH` if installed, otherwise the pinned
   `ghcr.io/gitleaks/gitleaks` image via Docker (offline, `--network none`), otherwise it prints a warning.

Run it by hand any time with `bash scripts/check-secrets.sh` (checks what is staged). CI runs gitleaks over the full
history on every push and pull request.

False positive? Prefer a placeholder (`<password>`, `${VAR}`); as a last resort add an inline `gitleaks:allow` comment
and explain why in the commit message. **Never** bypass the hook for a real value. If a secret ever reaches GitHub,
stop, rotate the credential and record it in `docs/STATUS.md` — public history is never rewritten.

Compose secrets live in a gitignored `deploy/.env` (from `deploy/.env.example`, milestone 0.4).

## Conventions

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) — `feat(api): …`, `fix(web): …`,
  `chore: …`, `docs: …`, `test: …`.
- **Branches / PRs:** one PR per milestone (`milestone/<n>-<slug>`), squash-merged when CI is green.
- **Line endings:** LF everywhere (`.gitattributes`), except `*.cmd` / `*.bat`.
- **Package versions:** only in `Directory.Packages.props` (NuGet, central package management) and
  `web/package-lock.json`.
- **GitHub Actions:** pin every action by full commit SHA with the version in a comment; declare `permissions:`
  explicitly; `persist-credentials: false`; never `pull_request_target`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `npm ci` fails with `EBADENGINE` | Node isn't 24: `nvm use 24.21.0` (or run a script, which switches for its session). |
| `Port 5080/5173 is already in use` | Another dev server or the dev compose is running; stop it (only one copy at a time). |
| Integration tests fail to start a container | Start Docker, or set `CONTROLHOME_TEST_SQLSERVER`. |
| `/api/*` returns `503 db.unavailable` | No reachable database yet: check the user-secret and that SQL Server 2025 is up. |
| Commit blocked by `check-secrets` | Read the file list it prints; move the value to user-secrets / `.env`, or use a placeholder. |
| Hook doesn't run at all | `git config core.hooksPath` must print `.githooks` (run the setup script). |
