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
- 2026-10-20 — Security update PR #13 (.NET 10.0.13, Pkcs/Xml advisories), tagged **v0.0.3**.
- 2026-10-21 — Milestone 1.2 merged (PR #14): capability catalogue, connector SDK, supervisor, Simulated + MQTT connectors (Zigbee2MQTT, HA discovery, Tasmota), StateProcessor + SignalR, runtime lease, safety-critical commands with step-up PIN, devices UI.
- 2026-10-21 — Milestone 1.4-A merged (PR #15): StateLog, 5-minute/hourly stats, EnergyHourly, rollups, retention purge, history API.
- 2026-10-21 — Milestone 1.3 + 1.4-B merged (PR #17): device placement + room auto-assign, integration flows + merge rules (orphaned, replace device), more Simulated types, demo home, backup export/import (passphrase), dashboard v1, history charts, settings pages (home, data retention, backup). 1558 .NET tests, 898 web tests, 83 E2E + 548 screenshots.
- 2026-10-21 — Tagged **v0.1.0** (Phase 1 complete).
- 2026-10-21 — Milestone 1.5 merged (PR #19): Umbrel community store template (`deploy/umbrel/`), Dockerfile NuGet layer fix, VersionPrefix 0.2.0.

## In progress (2026-10-21)
- **PAUSED 2026-10-21 ~14:58 UTC for the 5-hour usage limit (88%; resets 17:50 UTC).** Phase 2 wave A workflow `wayv3mbur` (run wf_ea0b675b-d0e, script workflows/scripts/p2a-vendor-connectors-wf_ea0b675b-d0e.js, base c4a0238) was stopped while all three implementers were in final verification; each had committed its work (worktrees clean):
  - LG ThinQ: worktree wf_ea0b675b-d0e-1, branch worktree-wf_ea0b675b-d0e-1, ef14c87 (+ d803678 energy windows into EnergyHourly).
  - DahuaHttp + go2rtc backend: worktree wf_ea0b675b-d0e-2, branch worktree-wf_ea0b675b-d0e-2, 9c8ed59 (+ c45129d).
  - SmartThings: worktree wf_ea0b675b-d0e-3, branch worktree-wf_ea0b675b-d0e-3, d4bcbbb.
  Resume plan (no implement step completed, so resumeFromRunId has nothing cached): edit the script so stage 1 is a short "finish" agent per track (reset to the commit above, check the task scope is complete, run the full suite, report), then the adversarial review stage as written.
- 1.3 follow-ups (not blocking): concurrent PUT of placements can return 500; backup option to include history; redacting logger for flow inputs; database size estimate on Data retention; kebab menu for Remove on mobile; small-room labels truncated on phone floor plans.
- Next phases: Phase 2 (docs/plans/phase-2-vendor-connectors.md: LG ThinQ, DahuaHttp + go2rtc, SmartThings), Phase 3 automation (draft in session scratchpad phase-3-automation.md — copy into docs/plans when starting).
- Usage guard: session cron every 10 min, pause at ≥ 90% of the 5-hour limit (TaskStop running tasks, one-shot resume after reset); no new workflows at ≥ 92% weekly.
- Host notes: default node is now v22 — prepend C:/Users/samur/AppData/Local/nvm/v24.21.0 to PATH for web commands.

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
- Umbrel: create a public `controlhome-umbrel-store` repo and copy `deploy/umbrel/store/` into it (steps in `deploy/umbrel/README.md`); then add it in umbrelOS (App Store → Community App Stores).
- Later: create a least-privilege `ControlHome_App` SQL login for production.
- SmartThings may require the paid Personal Plan ($4.99/mo) — decide when we reach milestone 2.3.

## Known failures / environment notes
- Docker Desktop VM has ~2 GB RAM: run local tests with `scripts/test-docker.ps1 -UseDevServer` (no local SQL container); E2E uses the dev SQL Server (`scripts/e2e.ps1`). Optional (Needs you): raise Docker Desktop memory to ≥ 4 GB.
- **Windows Smart App Control is ON** on the dev machine and has blocked freshly built `ControlHome.*.dll` files (per file hash).
  Workaround: architecture tests read DLLs from disk; run integration tests/app in Docker (Linux) when blocked. CI (Ubuntu) unaffected.
  Changing this Windows security setting is the user's decision (it usually cannot be re-enabled without reinstalling Windows).
