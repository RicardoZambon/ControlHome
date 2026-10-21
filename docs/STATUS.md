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

## In progress (resumed 2026-10-20 23:57 local)
- Milestone 1.2 (+1.4-C MQTT) complete on `milestone/1.2-devices-core` (PIN-UI fix bdc2be3, E2E 57 passed) — PR open. Running: `w37smutej` = 1.4-A history storage + API (run wf_b2e2060c-03b, base 157e837). Usage guard cron re-created.
- Branch `milestone/1.2-devices-core` (local, not yet pushed): main (incl. #13 .NET 10.0.13 hotfix, tag v0.0.3) + 1.2 backend + 1.2 review fixes (d1d4333) + Logging.Abstractions 10.0.13 + 1.4-C MQTT connector (merge 3c858f9). Full .NET suite green: 1373/1374.
- PAUSED run to resume: 1.2-E devices UI — `Workflow({scriptPath: <session>/workflows/scripts/m1-2-devices-ui-wf_b05b2e2a-c7e.js, resumeFromRunId: "wf_5f5ef08c-70b"})`; build (5d0f070), e2e (f169b66) and visual review are cached, only the final fix agent re-runs.
- After it finishes: merge its final branch into milestone/1.2-devices-core, regenerate OpenAPI, run tests + E2E, push, PR (merge commit), tag after CI.
- Then: 1.3 (placement, integrations flow UI, dashboard v1, demo seed, export/import) and 1.4-A/B (history + charts). Phase 2/3 plans drafted in the session scratchpad (phase-2 committed in docs/plans; phase-3 draft at scratchpad/phase-3-automation.md).
- Usage guard: recurring 10-min check (re-create on resume) pauses at ≥ 90% of the 5-hour limit.

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
