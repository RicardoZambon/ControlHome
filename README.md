<div align="center">

# ControlHome

**Self-hosted home control and automation for a real, mixed-vendor home.**
One dashboard, live floor plans and local automations for Samsung SmartThings, LG, Intelbras, Shelly, MQTT/Zigbee and
more — running on your own server, on your own LAN, with no cloud account of ours and no Home Assistant dependency.

[![CI](https://github.com/RicardoZambon/ControlHome/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/RicardoZambon/ControlHome/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Vue 3](https://img.shields.io/badge/Vue-3.5-42b883?logo=vuedotjs&logoColor=white)
![SQL Server 2025](https://img.shields.io/badge/SQL%20Server-2025-CC2927?logo=microsoftsqlserver&logoColor=white)
![Status: pre-alpha](https://img.shields.io/badge/status-pre--alpha-orange)

[Architecture](docs/architecture.md) · [Roadmap](docs/roadmap.md) · [Development guide](docs/development.md) · [Decisions](docs/adr/README.md)

</div>

> **Status: pre-alpha.** The foundation (Phase 0) is being built in the open; nothing is usable end to end yet.
> Follow the [roadmap](docs/roadmap.md) — the first image (`v0.0.1`) ships at the end of Phase 0.

## Features

| Feature | Planned in |
|---|---|
| **Floor plans** — draw rooms on a snapping grid over a traced image, drag devices in, see live state, tap to control | Phase 1 |
| **Native connectors** per vendor behind one SDK, plus generic **MQTT** (Zigbee2MQTT, Tasmota, HA-discovery intake) | Phases 1–6 |
| **Energy** — hourly kWh per device, BRL cost (flat, Tarifa Branca, bandeiras), projections, standby detection | Phases 1, 5 |
| **Automations and scenes** — time, sun, state and threshold triggers, conditions, run traces, loop protection | Phase 3 |
| **Safety first** — step-up PIN for unlock / open gate / disarm, at-most-once delivery, full audit log | Phase 1 |
| **Household accounts** — Admin / Member / Viewer, first-run setup wizard, kiosk mode | Phases 0, 5 |
| **pt-BR and English**, light and dark themes, works on phones | Phase 0 |
| **One Docker image** (amd64 + arm64) for Umbrel or any Docker host; SQL Server 2025 bundled or your own | Phase 0 |

**Integrations (planned):** Samsung SmartThings (+ Mibo Smart) · LG ThinQ appliances and webOS TVs · Intelbras cameras,
NVRs and access terminals (local HTTP/RTSP/ONVIF via go2rtc) · Intelbras Izy / Tuya (local LAN control) · Shelly ·
MQTT / Zigbee2MQTT · network presence · Matter. See the [integration matrix](docs/architecture.md#14-integration-matrix).
Every connector is labelled *Experimental: not live-validated* until it has been checked against real devices.

## Quickstart

### Development

You need the **.NET SDK 10.0.302**, **Node 24.21.0** (via nvm), **Docker** and **PowerShell 7** (or bash).
A SQL Server 2025 instance is needed to run the app (not for the tests).

```powershell
git clone https://github.com/RicardoZambon/ControlHome.git
cd ControlHome
./scripts/setup-dev.ps1 -PromptConnectionString   # hooks, tools, packages; stores your dev DB connection in user-secrets
./scripts/dev.ps1                                 # API on :5080 + Vite on :5173 — open http://localhost:5173
```

```bash
dotnet build -warnaserror && dotnet test          # backend
cd web && npm run lint && npm run typecheck && npm test && npm run check:colors && npm run build   # frontend
```

On Linux/macOS without PowerShell, use `scripts/setup-dev.sh --prompt-connection-string`, then run
`dotnet watch run --project src/ControlHome.Api --launch-profile http` and `npm run dev --prefix web` in two terminals.
Details, database safety rules and troubleshooting: **[docs/development.md](docs/development.md)**.

### Docker (from `v0.0.1`)

The image and compose files land with milestone 0.4. Then:

```bash
cp deploy/.env.example deploy/.env       # fill in the values; .env is gitignored
docker compose -f deploy/docker-compose.yml up -d                          # bring your own SQL Server 2025
docker compose -f deploy/docker-compose.yml --profile bundled-db up -d     # or add SQL Server 2025 Express (x64 hosts)
docker compose -f deploy/docker-compose.yml logs controlhome | grep -i "setup code"
```

Open `http://<your-server>:8711`, enter the one-time setup code, and the wizard walks you through the database (if not
configured), the admin account and your home. Images: `ghcr.io/ricardozambon/controlhome` (`edge` from `main`, semver
tags for releases). An Umbrel community app follows in milestone 1.5.

## How it's built

- **Backend:** ASP.NET Core 10 minimal APIs, EF Core 10, ASP.NET Core Identity, SignalR — a modular monolith with an
  enforced reference graph (`Domain` ← `Application` ← `Infrastructure` ← `Api`; connectors depend only on their SDK).
- **Frontend:** Vue 3.5, Vite 8, TypeScript 6, Tailwind 4 + shadcn-vue/Reka UI with a single OKLCH token source,
  Pinia, vue-i18n; API client generated from the committed OpenAPI document.
- **Data:** SQL Server 2025 (native `json`, compatibility level 170), write-behind state history with rollups and a
  single hourly energy table.
- **Security:** LAN-only allowlist, same-origin + `X-ControlHome` checks on every mutation, no CORS, CSP, certificate-
  protected Data Protection keys, gitleaks in CI and in a pre-commit hook.

Read the full design in **[docs/architecture.md](docs/architecture.md)** and the reasoning in
**[ADR 0001](docs/adr/0001-stack-and-architecture.md)**.

## Contributing

Issues and ideas are welcome. Please read [docs/development.md](docs/development.md) first; commits follow
Conventional Commits, and the pre-commit secret check must stay enabled — this repository is public and controls a real
home. Found a security problem? Please contact the maintainer privately instead of opening a public issue.

## License

Licensed under the [Apache License 2.0](LICENSE).
