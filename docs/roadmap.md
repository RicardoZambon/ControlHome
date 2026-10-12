# ControlHome roadmap

Each **milestone is one PR**; each **phase ends with a tag**. Checkboxes are ticked when the milestone's PR is merged
with green CI. The current resume point and anything blocked on the owner live in [`STATUS.md`](STATUS.md); the
detailed task plan for each phase lives in [`plans/`](plans/); the design is in [`architecture.md`](architecture.md).

Named specs are Playwright E2E specs that must pass for the milestone (plus the global gates: no console errors, no
serious/critical axe issues, no missing i18n keys, no horizontal overflow at 375 px).

Every connector ships labelled **"Experimental: not live-validated"** until the owner validates it with their own
account (`docs/connectors/LIVE-VALIDATION.md`); that never blocks the roadmap.

---

## Phase 0 — Foundation → tag `v0.0.1`

Plan: [`plans/phase-0-foundation.md`](plans/phase-0-foundation.md)

- [ ] **0.1 Skeleton and loop** *(in progress)*
  - Repo basics: `CLAUDE.md`, extended `.gitignore`, `global.json` / `.nvmrc` / `.npmrc`, the props files, tool
    manifest, check-secrets pre-commit hook, `.gitleaks.toml`.
  - Projects: `ControlHome.slnx` with Domain / Application / Infrastructure / Api and tests; `/health` and the SPA
    fallback; architecture tests for the reference graph.
  - Web: npm workspace with design tokens plus 3 primitives (button, card, input) in `@controlhome/ui`.
  - Tooling: `ci.yml` (build / lint / test / secret scan), `.claude/launch.json` (api on 5080, web on 5173 with proxy),
    the dev scripts, README quickstart, `development.md`, `architecture.md`, ADR 0001.
- [ ] **0.2 Data and auth API**
  - DbContext, Identity, initial migration, SQL Server 2025 check, database readiness (503 `db.unavailable` until ready).
  - Setup lifecycle and setup code; login / logout / me; request-edge protections; rate limits; roles; users API.
  - ProblemDetails with `code`, Serilog (redaction lands with the first secret-bearing connector in 1.2), Data
    Protection with a certificate.
  - Committed `openapi.json` + generated client + drift check; test-environment endpoints. Integration tests.
- [ ] **0.3 Shell and auth UI**
  - App shell, theme (light / dark / system), i18n (pt-BR + en), setup wizard, login, users admin, profile,
    `/dev/ui-kit`.
  - Specs: `setup`, `login`, `users-admin`, `theme-i18n`; screenshot runner.
- [ ] **0.4 Packaging**
  - Dockerfile (multi-arch, chiseled, digest-pinned), prod and dev compose, pre-DB setup mode, CI compose smoke,
    `release.yml` (`edge`), CodeQL, Dependabot.
  - **Exit:** in CI, the prod compose with `bundled-db` passes the setup → login spec, the image completes DB setup
    from the UI with no connection string, and it survives the database starting late.
  - Tag **`v0.0.1`**.

## Phase 1 — Home model, devices core, floor plans → tag `v0.1`

- [ ] **1.1 Floors, rooms and the floor-plan editor** (no devices yet). Spec: `floorplan-edit`.
- [ ] **1.2 Devices core**
  - Capability catalogue, connector SDK, integration supervisor, Simulated connector (5 types).
  - StateProcessor + SignalR, single-runtime lock.
  - DeviceCommandService with the safety policy (step-up PIN) + audit.
  - Devices list and detail with the capability-control registry; shape-fixture mapper tests.
  - Specs: `device-control`, `realtime-two-clients`, `safety-pin`.
- [ ] **1.3 Devices on the plan and integrations flow**
  - Device placement and room auto-assign; dashboard v1 (favorites, rooms, power now, offline).
  - Integrations flow (catalog → flow → discover → import, with merge rules); the remaining Simulated types; demo seed.
  - Logical export / import (scheduled to `/data/backups`).
  - Specs: `integration-import`, `plan-placement`, `export-import`.
- [ ] **1.4 History and MQTT**
  - History with rollups, retention, `EnergyHourly` and ECharts history views; MQTT connector.
  - Spec: `history-chart`. Tag **`v0.1`**.
- [ ] **1.5 Umbrel template + store-repo instructions** (non-blocking, `v0.1.x`).

## Phase 2 — Vendor connectors, wave 1 → tag `v0.2`

- [ ] **2.1 LG ThinQ** (plus the optional HTTPS listener).
- [ ] **2.2 DahuaHttp + go2rtc** (Intelbras cameras / NVR / access): CameraPlayer (WebRTC → MSE), camera tiles on the
  dashboard and plan, ONVIF fallback.
- [ ] **2.3 SmartThings** (Samsung + Mibo Smart devices), including the `PlanRequired` state.
- Per connector: upstream-derived fixture tests (each with a `SOURCE.md`), flow screenshots, docs. Tag **`v0.2`**.

## Phase 3 — Automation → tag `v0.3`

- [ ] **3.1 Engine MVP**: scenes, a form-based builder, run traces, templates ("sunset → lights", "washer finished →
  notify"). Spec: `sunset-automation` (advances `/api/_test/clock`). Tag **`v0.3`**.
- [ ] **3.2 Full engine**: home modes, persons / presence (manual and device-based), notification center.

## Phase 4 — Vendor connectors, wave 2 → tag `v0.4`

- [ ] **4.1 LG webOS** (TVs, local SSAP + Wake-on-LAN).
- [ ] **4.2 Shelly + gate relay pattern.**
- [ ] **4.3 Tuya / Izy**: LAN protocol tested against tinytuya frame vectors, cloud keys/specs, region picker, re-pair
  wizard, Izy Zigbee sensors through the cloud.
- [ ] **4.4 NetworkPresence** (ARP/DHCP generic; Twibi experimental).
- Tag **`v0.4`**.

## Phase 5 — Energy and experience → tag `v0.5`

- [ ] **5.1 Energy dashboard**: kWh and BRL cost (flat, Tarifa Branca, bandeira surcharges), projections, top
  consumers, standby detection — all built only on `EnergyHourly`.
- [ ] **5.2 Dashboards and kiosk**: custom per-user dashboards (widget grid), kiosk mode (revocable device token), an
  Open-Meteo weather widget and triggers.
- [ ] **5.3 Notifications**: Telegram, ntfy, and web push (on HTTPS), plus alert rules (offline device, door left open,
  high consumption, appliance done).
- [ ] **5.4 Access and diagnostics**: passkeys / TOTP (on HTTPS) and an integration diagnostics page.
- Tag **`v0.5`**.

## Phase 6 — Matter and v1.0 → tag `v1.0`

- [ ] **6.1 Matter connector** (matterjs-server sidecar, multi-admin codes).
- [ ] **6.2 Hardening**: security review, WCAG AA, performance (history queries, hub fan-out), full E2E, user docs in
  pt-BR and English, and store publishing. Tag **`v1.0`**.

---

## Integration schedule at a glance

| Milestone | Integration |
|---|---|
| 1.2–1.3 | Simulated (dev, demo, E2E) |
| 1.4 | MQTT: Zigbee2MQTT, HA-discovery intake, Tasmota |
| 2.1 | LG appliances (ThinQ Connect) |
| 2.2 | Intelbras cameras / NVR / SS access, XPE intercom (flag) |
| 2.3 | Samsung SmartThings (+ Mibo Smart) |
| 4.1 | LG TVs (webOS) |
| 4.2 | Shelly (incl. gate relay + reed sensor) |
| 4.3 | Intelbras Izy (Tuya LAN + cloud keys), IGD 110 gate, Izy Zigbee sensors |
| 4.4 | Network presence (ARP/DHCP, Twibi experimental) |
| 6.1 | Matter |

Details and risks: [architecture § Integration matrix](architecture.md#14-integration-matrix).
