# 0001. Stack and architecture: .NET 10 modular monolith, Vue + shadcn-vue design tokens, SQL Server 2025, native connectors

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** repository owner (hard constraints, device list, hosting), planning session (stack details)

## Context

ControlHome is a self-hosted home-control and automation web app for one household. It runs in Docker on an Umbrel
home server, on the LAN only, with household accounts (Admin / Member / Viewer), a first-run setup wizard, and pt-BR +
English UI. The repository is public (Apache-2.0) and the app controls a real home, including doors, gates and an
alarm.

Fixed by the owner:

- Backend in **.NET (C#)**, frontend in **Vue.js**, database **Microsoft SQL Server** (an SQL Server 2025 instance
  already runs on the Umbrel and hosts other databases).
- Devices: Samsung/SmartThings, LG (appliances and TVs), several Intelbras product families (cameras/NVR, Izy smart
  devices, intercom, gate automation, mesh Wi-Fi), and generic/DIY devices.
- Native connectors per vendor plus generic MQTT — **no Home Assistant dependency**.

Facts from research (October 2026) that constrain the choices:

- PrimeVue 5 became proprietary; shadcn-vue, Reka UI and Tailwind 4 are MIT.
- Recent MediatR, AutoMapper and FluentAssertions versions are commercial.
- vue-tsc does not support TypeScript 7 yet; Vitest 5 and the OpenAPI client generators need Node ≥ 22.
- SQL Server container images are x64-only, while Umbrel hardware can be arm64.
- Several integrations need the host network (discovery, Wake-on-LAN, device push events); vendor clouds differ
  widely (SmartThings single-use rotating refresh tokens and a possible paid plan, LG ThinQ Connect with outbound MQTT,
  Intelbras Izy being a Tuya OEM with a 10-device free cloud limit, Dahua-style local CGI for cameras).

## Decision

1. **Backend: a .NET 10 modular monolith** (ASP.NET Core minimal APIs, EF Core 10) with a strict reference graph —
   `Domain` (no deps) ← `Application` ← `Infrastructure` ← `Api`; later `Connectors.Abstractions` and `Automation`
   depend on `Domain` only, and each `Connectors.<Vendor>` depends on `Connectors.Abstractions` only. Architecture
   tests enforce it. Plain services + DI, DTOs as records defined once in `Application`, hand mapping, ProblemDetails
   with stable `code`s. No MediatR / AutoMapper / FluentAssertions / Moq (tests: xunit.v3, Shouldly, NSubstitute).
   One process serves the API, SignalR hubs and the SPA from the same origin.
2. **Frontend: Vue 3.5 + Vite 8 + TypeScript ~6.0 + Tailwind 4 + shadcn-vue/Reka UI**, in an npm workspace on Node 24
   LTS. A single design source: OKLCH tokens in `@controlhome/ui` (`tokens.css`, light + dark), Tailwind's default
   palette removed, primitives only from the ui package, enforced by ESLint and a raw-color CI check. vue-i18n with
   pt-BR and English; the API client is generated from the committed OpenAPI document.
3. **Database: SQL Server 2025 is required** (compatibility level 170, native `json` type), kept **external** to the
   app image. The app works with an existing server (Umbrel's) or an optional bundled SQL Server 2025 Express
   container (x64). Tooling may only create/drop databases named `ControlHome_(Dev|Test|Compose)…`.
4. **Integrations: native connectors per vendor** behind one connector SDK (descriptors, flows, runtimes, a context
   for state/events/availability), plus generic MQTT. Sidecars only where a mature open implementation exists (go2rtc
   for cameras, matterjs-server for Matter). **No Home Assistant dependency**; Home Assistant's MQTT discovery format is
   accepted for intake only. A built-in Simulated connector drives development, demos and E2E tests.
5. **Own automation engine and scheduler** (Home Assistant semantics, built on Cronos + SunCalcNet) instead of
   Quartz 4 or an external rules engine.
6. **Delivery: one multi-arch Docker image** (`linux/amd64` + `linux/arm64`, chiseled non-root runtime, digest-pinned
   bases) with production compose on the host network, an Umbrel community app in a separate store repo, and GitHub
   Actions for CI, release, CodeQL and Dependabot.

## Consequences

- **Easier:** one deployable unit and one origin (no CORS, simple cookies); fast local development (API on 5080, Vite
  on 5173 proxying to it); a reference graph that keeps vendor code out of the core; every UI color and primitive
  traceable to one package; licences that stay permissive.
- **Harder / we must now:**
  - Implement and maintain every vendor protocol ourselves (Tuya LAN, ThinQ MQTT, SmartThings OAuth, Dahua CGI, webOS)
    and test them without real credentials — upstream-derived fixtures with recorded provenance, WireMock, Mosquitto
    Testcontainers, and the Simulated connector. Connectors stay "Experimental: not live-validated" until the owner
    validates them.
  - Own the automation semantics (loop protection, restart behaviour, time travel in tests) instead of inheriting
    Home Assistant's.
  - Require SQL Server 2025 (startup check for version ≥ 17 and compatibility level 170); users on older servers must
    upgrade or use the bundled container. The app must survive a database that is missing, late or unreachable
    (readiness state, 503 `db.unavailable`, pre-DB setup wizard).
  - Run only one active runtime per database (`sp_getapplock`) so single-use tokens and automations are never
    duplicated; a second instance is a read-only standby — there is no high-availability story.
  - Keep secrets out of a public repo (user-secrets, gitignored `.env`, gitleaks rules, pre-commit hook) and protect
    the home (request-edge origin checks, LAN allowlist, step-up PIN for safety-critical commands, audit log).
- **Revisit if:** vue-tsc gains TypeScript 7 support (upgrade), SQL Server ships arm64 images (bundled DB on arm64), a
  vendor publishes an official local API that replaces a reverse-engineered path, or a second household/site becomes a
  requirement (multi-tenant is out of scope).
