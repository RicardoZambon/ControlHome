# ControlHome — Status

Resume point for whoever (human or agent) continues the work. Updated after every milestone.

## Done
- 2026-10-11 — Research (vendor APIs, stack) + approved phased plan (see `docs/architecture.md`, `docs/roadmap.md`).
- 2026-10-11 — Preflight: Node 24.21.0 active via nvm-windows; Docker Desktop running (29.7.2, linux/x86_64);
  `ControlHome_Dev` created on umbrel SQL Server 2025 (compat 170, SIMPLE recovery); workflow-file push verified.
- 2026-10-12 — Milestone 0.1 merged (PR #1): .NET skeleton, web workspace + @controlhome/ui tokens, CI, docs.
- 2026-10-12 — Milestone 0.2 merged (PR #2): persistence, readiness, Data Protection, setup/auth/users/home/audit, request edge; adversarial review fixed 18 issues.
- 2026-10-12 — Milestone 1.1 merged (PR #9): floors, rooms, SVG floor-plan editor. Hotfix PR #10 (multi-arch BuildKit cache lock, deterministic test). Tagged **v0.0.2** (ghcr.io/ricardozambon/controlhome:0.0.2, public, amd64+arm64).
- 2026-10-12 — Tagged **v0.0.1** (Phase 0 complete); release workflow publishes ghcr.io/ricardozambon/controlhome (amd64 + arm64).
- 2026-10-12 — Milestones 0.3 + 0.4 (PR #3): ui primitives, app shell + auth UI (en/pt-BR), Playwright E2E + screenshots + visual review, Docker image/compose/release/CodeQL/Dependabot, pre-DB setup mode.

## In progress (2026-10-20)
- Branch `milestone/1.2-devices-core` = main + 1.2 backend (merge 9851968). Uncommitted: fixes from the 1.2 adversarial review (workflow `m1.2-review`, run wf_488b45fd-359) incl. SignalR revocation (R1) and supervisor tests (R2).
- 1.2-E devices UI pipeline (run wf_5f5ef08c-70b) and 1.4-C MQTT connector (run wf_bfd9a31d-0d4) in isolated worktrees, both based on 9851968.
- Security hotfix branch `fix/dotnet-10.0.13-security` (agent): .NET 10.0.13 packages + transitive System.Security.Cryptography.Pkcs/Xml pins + Docker base images; CI on main is red until it merges (NuGet audit NU1902/NU1903).
- Next: merge hotfix → tag v0.0.3; merge review fixes + UI + MQTT into 1.2 → PR; then 1.3 (placement, integrations flow, dashboard v1, demo seed, export/import), 1.4-A/B (history).
- Running background tasks (for the usage guard / TaskStop): `wefx2u4p8` = m1.2-review (run wf_488b45fd-359), `wo9tf5o8m` = 1.2-E devices UI (run wf_5f5ef08c-70b), `wkfargr1c` = 1.4-C MQTT (run wf_bfd9a31d-0d4). Security hotfix PR #13 open (branch fix/dotnet-10.0.13-security, commit 40ae961).
- Work pauses when the 5-hour usage limit reaches 95% (a session cron `[usage-guard]` checks every 10 min and pauses at ≥ 90%); resume after reset.

## Decisions taken (defaults chosen without the user)
- Package pins: Microsoft.* 10.0.12 (local runtime is 10.0.10; servicing-compatible). TypeScript pinned `~6.0.3` (npm latest is 7.x, unsupported by vue-tsc).
- Line endings normalized to LF via `.gitattributes` (repo is Docker/Linux-first; user has `core.autocrlf=true`).
- Container port 8711 (8099 is taken by the Umbrel "wavelog" app); dev API 5080, Vite 5173.
- `dotnet test` uses Microsoft.Testing.Platform (global.json `test.runner`) — required by xunit.v3 4.x on the .NET 10 SDK.
- Icons: `@lucide/vue` (lucide-vue-next is deprecated; shadcn-vue 2.8 emits @lucide/vue).
- Root `nuget.config` clears machine sources (nuget.org only, with package source mapping).
- Host-name validation (DNS rebinding): IP literals, localhost, single-label and .local/.lan/.home.arpa/.internal names are allowed by default; add others (e.g. Tailscale *.ts.net) via Network__AllowedHostNames.
- Configured Setup:Code must be ≥ 12 chars and is never logged; generated codes rotate after 20 failures.
- Milestones 0.3 and 0.4 shipped in one PR (stacked work).
- Milestone PRs are merged with **merge commits** (not squash) from PR #3 on: work is stacked across parallel worktrees, and squash merges break the shared history (add/add conflicts).
- Vendor research snapshot committed as `docs/research/vendor-apis-2026-10.md`; Phase 2 plan in `docs/plans/phase-2-vendor-connectors.md`.
- Log-redaction of connector secrets (`KnownSecretsRedactor`) lands with the first secret-bearing connector (milestone 1.2), not 0.2.

## Needs you (non-blocking)
- Live validation of each vendor connector with your own accounts (`docs/connectors/LIVE-VALIDATION.md`, from Phase 2).
- Optional: enable branch protection on `main` + secret-scanning push protection (repo settings).
- Later: create the `controlhome-umbrel-store` repo; create a least-privilege `ControlHome_App` SQL login for production.
- SmartThings may require the paid Personal Plan ($4.99/mo) — decide when we reach milestone 2.3.

## Known failures / environment notes
- Docker Desktop VM has ~2 GB RAM: run local tests with `scripts/test-docker.ps1 -UseDevServer` (no local SQL container); E2E uses the dev SQL Server (`scripts/e2e.ps1`). Optional (Needs you): raise Docker Desktop memory to ≥ 4 GB.
- **Windows Smart App Control is ON** on the dev machine and has blocked freshly built `ControlHome.*.dll` files (per file hash).
  Workaround: architecture tests read DLLs from disk; run integration tests/app in Docker (Linux) when blocked. CI (Ubuntu) unaffected.
  Changing this Windows security setting is the user's decision (it usually cannot be re-enabled without reinstalling Windows).
