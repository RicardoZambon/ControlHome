# ControlHome — Status

Resume point for whoever (human or agent) continues the work. Updated after every milestone.

## Done
- 2026-10-11 — Research (vendor APIs, stack) + approved phased plan (see `docs/architecture.md`, `docs/roadmap.md`).
- 2026-10-11 — Preflight: Node 24.21.0 active via nvm-windows; Docker Desktop running (29.7.2, linux/x86_64);
  `ControlHome_Dev` created on umbrel SQL Server 2025 (compat 170, SIMPLE recovery); workflow-file push verified.
- 2026-10-12 — Milestone 0.1 merged (PR #1): .NET skeleton, web workspace + @controlhome/ui tokens, CI, docs.

## In progress
- Milestone 0.2 — Data and auth API (draft PR #2): foundation committed (persistence, readiness, Data Protection,
  ProblemDetails, OpenAPI + api-client, contracts). In parallel: request edge, setup/auth/users/home/audit,
  0.3-A ui primitives, 0.4 packaging (isolated worktrees).

## Decisions taken (defaults chosen without the user)
- Package pins: Microsoft.* 10.0.12 (local runtime is 10.0.10; servicing-compatible). TypeScript pinned `~6.0.3` (npm latest is 7.x, unsupported by vue-tsc).
- Line endings normalized to LF via `.gitattributes` (repo is Docker/Linux-first; user has `core.autocrlf=true`).
- Container port 8711 (8099 is taken by the Umbrel "wavelog" app); dev API 5080, Vite 5173.
- `dotnet test` uses Microsoft.Testing.Platform (global.json `test.runner`) — required by xunit.v3 4.x on the .NET 10 SDK.
- Icons: `@lucide/vue` (lucide-vue-next is deprecated; shadcn-vue 2.8 emits @lucide/vue).
- Root `nuget.config` clears machine sources (nuget.org only, with package source mapping).
- Log-redaction of connector secrets (`KnownSecretsRedactor`) lands with the first secret-bearing connector (milestone 1.2), not 0.2.

## Needs you (non-blocking)
- Live validation of each vendor connector with your own accounts (`docs/connectors/LIVE-VALIDATION.md`, from Phase 2).
- Optional: enable branch protection on `main` + secret-scanning push protection (repo settings).
- Later: create the `controlhome-umbrel-store` repo; create a least-privilege `ControlHome_App` SQL login for production.
- SmartThings may require the paid Personal Plan ($4.99/mo) — decide when we reach milestone 2.3.

## Known failures / environment notes
- **Windows Smart App Control is ON** on the dev machine and has blocked freshly built `ControlHome.*.dll` files (per file hash).
  Workaround: architecture tests read DLLs from disk; run integration tests/app in Docker (Linux) when blocked. CI (Ubuntu) unaffected.
  Changing this Windows security setting is the user's decision (it usually cannot be re-enabled without reinstalling Windows).
