# Phase 2 — Vendor connectors, wave 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Real devices from the user's main brands work through the Phase 1 connector SDK: LG appliances (ThinQ Connect),
Intelbras cameras/NVR/access terminals/door stations (Dahua-style HTTP + RTSP via go2rtc) and Samsung SmartThings
(OAuth-In + adaptive polling). Each connector is fully tested against upstream-derived fixtures and ships as
"Experimental — not live-validated" until the user validates it with their own accounts.

**Architecture:** One project per connector (`ControlHome.Connectors.LgThinQ`, `.DahuaHttp`, `.SmartThings`) referencing only
`Connectors.Abstractions`. Vendor payloads are mapped to canonical capabilities by pure mapper classes (unit-tested with
fixtures); transport code (HTTP/MQTT) is isolated behind small typed clients (WireMock.Net / local Mosquitto in tests).
Cameras use a go2rtc sidecar registered through `ICameraStreamRegistry`; the browser only talks to our backend.

**Tech Stack:** Phase 1 + MQTTnet 5.2.0.1603, Microsoft.Extensions.Http.Resilience 10.x, System.Threading.RateLimiting,
WireMock.Net 2.19.0, Testcontainers (eclipse-mosquitto:2.1.2 with TLS client certs), go2rtc 1.9.x (alexxit/go2rtc, digest-pinned),
YARP (Yarp.ReverseProxy) or a minimal WebSocket relay for the camera proxy.

**Spec:** approved plan · `docs/architecture.md` · `docs/research/vendor-apis-2026-10.md` (fact-checked vendor research —
read the section for your connector, including "Verification" and "Revised recommendation").

## Global Constraints
- Connectors reference only `ControlHome.Connectors.Abstractions`; no vendor SDK NuGets (all stale) — hand-written typed clients.
- Never ship or embed shared vendor credentials (client ids/secrets, PATs, API keys beyond documented public constants kept
  in config, e.g. LG `x-api-key`); every household brings its own. Never commit real tokens, MACs, serials, IPs or coordinates:
  fixtures are synthetic or derived from public upstream fixtures with provenance in `Fixtures/<Vendor>/SOURCE.md`.
- Secrets only via the SDK secret store (Data Protection) and per-device secret store; redaction registered for every secret.
- Commands: at-most-once for safety-critical (door/gate open); idempotency keys; no retries on non-idempotent vendor calls.
- Each connector: setup Flow(s) with localized (en/pt-BR) text, `LIVE-VALIDATION.md` checklist section, docs page
  `docs/connectors/<vendor>.md` (setup steps, limitations, troubleshooting), "Experimental" badge until validated.
- Rate budgets per integration persisted (QuotaCounter); errors classified (401 → NeedsReauth, plan/402 → PlanRequired, 429 →
  backoff honoring headers).

## Review Focus
1. Token/cert rotation races (SmartThings single-use refresh tokens; LG MQTT cert re-issue ≤ 1/min) — single-flight + persisted atomically.
2. Reconnect storms (MQTT drop, Dahua event stream drop) — exponential backoff with jitter; state resync after reconnect.
3. Vendor payload drift (unknown properties, missing fields) — mapper degrades to generic capability, never throws the runtime.
4. Camera credentials never leave the server (go2rtc API bound to 127.0.0.1, RTSP server disabled, proxied endpoints only).
5. Door/gate open commands fire exactly once even on timeouts (no hedging/retry; WireMock delayed-response test).

---

## Milestone 2.1 — LG ThinQ (+ optional HTTPS listener)
**Port of pythinqconnect (Apache-2.0; NOTICE attribution) — cross-check with jsthinqconnect.**
- Setup Flow: Form(PAT [secret], country (default BR), name) → validates with `GET /devices` → Done. Region from country table
  (BR → `https://api-aic.lgthinq.com`). One persistent client id per integration (`controlhome-<guid>`).
- HTTP: DelegatingHandler adds `Authorization: Bearer <PAT>`, `x-country`, `x-client-id`, `x-api-key` (config, default SDK
  constant), `x-service-phase: OP`, `x-message-id` (22-char url-safe base64), `x-conditional-control: true` on control; token
  bucket per PAT (conservative, ~1 req/10 s sustained, bursts of 5); error-code mapping (1103/1218 → NeedsReauth; 1304 terms;
  1306 → QuotaExceeded + 1 h backoff; 1307 wrong country; 1308/1309/2207/2301–2305 → user-readable command rejections).
- Discovery: `GET /devices` → `GET /devices/{id}/profile` (+ `/devices/energy/{id}/profile`); profile → capabilities generically
  (enum/range/boolean/number with r/w) + curated mappings for AC (switch, thermostat modes/setpoints/fan), washer/dryer/washtower
  (applianceCycle incl. remoteControlEnabled gate), refrigerator (temperature per location → endpoints), dishwasher, air purifier,
  dehumidifier, robot cleaner. Parent/child per research (WASHTOWER sub-reports → endpoints; WASHTOWER_WASHER/_DRYER → children).
- Realtime: `POST /client` (allowExist) → stored RSA-2048 key + cert (CSR CN=lg_thinq SHA-512 → `POST /client/certificate`,
  ≤ 1/min) → `GET /route` → MQTTnet TLS 8883 with client cert + bundled AmazonRootCA1, CleanSession=false, QoS 1; subscribe
  push/event per device (1207 = success), refresh every 24 h and on reconnect; DEVICE_STATUS merges partial reports
  (newest-wins), DEVICE_PUSH → events + notifications later. Reconnect is the normal path (backoff+jitter). Poll `/state` only at
  startup/reconnect/MQTT-down (≥ 5 min per device).
- Energy: nightly randomized job 01:00–05:59 local → DAILY yesterday + MONTHLY current/last → `EnergyReading` windows → EnergyHourly
  (flag not hourly); optional hourly "today" per device (off by default).
- Replace-token flow keeps devices/rooms/automations. DELETE `/client` only when the integration is removed.
- Tests: mapper fixtures from HA `lg_thinq` + pythinqconnect tests (SOURCE.md); WireMock for REST incl. error codes and quota;
  MQTT against Testcontainers Mosquitto with TLS client-cert auth (self-signed CA in test) — connect, subscribe, partial report merge,
  reconnect resubscribe.
- HTTPS listener (optional, `ControlHome:Https`): user PFX or self-signed generated on first run (`/data/keys/https.pfx`), Kestrel
  second endpoint; docs; secret/PIN forms show an "unencrypted connection" banner over plain HTTP.

## Milestone 2.2 — Intelbras cameras/NVR/access (DahuaHttp) + go2rtc
- Setup Flow per device: Form(host, port 80, user, password [secret], device kind auto-detect) → `magicBox.cgi?action=getDeviceType`
  + `getSoftwareVersion` (Digest auth via SocketsHttpHandler + CredentialCache "Digest") → checklist step (CGI enabled,
  compatibility auth/MD5) on 401/500 → Done. NVR: enumerate channels → camera child devices. Never retry 401 quickly
  (device lockout); read lockout policy where available.
- Events: one long-lived `eventManager.cgi?action=attach&codes=[All]&heartbeat=5` per device (multipart parser, filter locally:
  VideoMotion → motion, AlarmLocal → contact/alarm input, AccessControl → doorOpener event, CrossLine/CrossRegion → motion
  variants); reconnect with backoff. Inbound push receivers via `/api/hooks/{id}` for SS "Serviço de eventos" (optional) and
  syslog listener (off by default, UDP 5514, device IP allowlist).
- Door release: SS terminals `accessControl.cgi?action=openDoor&channel=1&UserID=101&Type=Remote` → `doorOpener.open`
  (safetyCritical, at-most-once). XPE 3200 HTTP relay `/fcgi/do?action=OpenDoor&UserName&Password&DoorNum=1` behind feature flag
  `Connectors:DahuaHttp:XpeRelay` (off) — marked "untested on hardware".
- Cameras: `camera` capability; `snapshot.cgi?channel=N` (probe 0/1 base) proxied as `/api/cameras/{id}/snapshot`; RTSP
  `rtsp://<user>:<pass>@host:554/cam/realmonitor?channel=N&subtype=0|1` registered at runtime in go2rtc via its API
  (never written to config, never sent to the browser). ONVIF fallback (WS-Discovery + GetStreamUri) for non-Dahua cameras.
- go2rtc sidecar (compose profile `cameras`): generated `go2rtc.yaml` (api on 127.0.0.1 with random credentials + local_auth,
  `rtsp.listen: ""`, `exec.allow_paths: []`, modules api/webrtc/mse/mjpeg, WebRTC candidates = LAN IP); backend
  `ICameraStreamRegistry` implementation + proxy `/api/cameras/{id}/ws` (MSE) and `/api/cameras/{id}/webrtc` (SDP exchange) with
  permission checks; integration test that no other go2rtc path is reachable.
- UI: `CameraPlayer` (WebRTC → MSE fallback using go2rtc's video-rtc.js protocol), camera tiles (snapshot refresh, tap to live),
  camera on floor plan, motion badge; door-open button with PIN step-up.
- Tests: Digest auth + multipart event stream against WireMock (fixtures from the Intelbras HTTP API V3.35 examples, synthetic);
  ONVIF SOAP fixtures; proxy authz tests; Simulated camera reuses the registry.

## Milestone 2.3 — SmartThings
- Setup Flow: instructions (data-driven, from `docs/connectors/smartthings.md`) to create the user's own OAuth-In app with the
  SmartThings CLI → Form(client id, client secret [secret], redirect URL (default https://httpbin.org/get with disclosure),
  auth/token URL presets (docs `/v1/oauth/*` default; HA preset)) → ExternalUrl(authorize URL with state) + PasteBack(redirected
  URL) → token exchange (Basic auth) → Choose(location if several) → Done. One integration per location. PAT mode (dev only,
  24 h banner).
- Token manager: single-flight refresh (SemaphoreSlim + DB rowversion) when < 3 h left and weekly keep-alive; persist rotated
  access + refresh atomically; 401/invalid_grant → NeedsReauth; 402/403-plan → PlanRequired banner.
- Polling: bulk `GET /v1/devices?locationId&includeStatus=true&includeHealth=true&includeRestricted=true` every 30–60 s
  (header-driven budget; ms/s ambiguity handled); hot-poll a device every 5 s for 30 s after commands (≤ 12/min/device);
  rooms cached (50/h limit); capabilities/presentations cached in DB (multi-day TTL).
- Mapping: switch, switchLevel, colorControl/colorTemperature, airConditionerMode/thermostatCoolingSetpoint/fanMode, sensors,
  lock (unlock safetyCritical), mediaPlayback/audioVolume/tvChannel → mediaPlayer, washerOperatingState/samsungce.* →
  applianceCycle (remoteControlStatus gate), powerMeter/energyMeter/powerConsumptionReport → power/energy (windows → EnergyHourly;
  one selected source per endpoint); components → endpoints; unmapped → generic; hide disabledCapabilities.
- Commands: `POST /v1/devices/{id}/commands` (`component` field, ≤ 10 per call), ACCEPTED = pending until state confirms
  (hot-poll); scenes as automation actions later.
- Tests: fixtures from HA `smartthings` tests (Apache-2.0, SOURCE.md), WireMock token refresh races (two concurrent → one refresh),
  rate-limit headers, PlanRequired classification, OAuth flow state/CSRF.
- Webhook mode (public HTTPS target) documented as a future option; not implemented in 2.3.

## Exit (Phase 2)
- All connector suites green; E2E: add-integration flows render and validate (WireMock-backed Testing connectors where needed);
  camera player spec against go2rtc in CI (test RTSP source: go2rtc's built-in `ffmpeg:` test pattern disabled → use a static
  sample via `exec` NOT allowed → use go2rtc test RTSP from a tiny mediamtx container in CI only).
- `docs/connectors/LIVE-VALIDATION.md` lists the user's checklist; STATUS "Needs you" updated. Tag v0.2.
