# Samsung SmartThings connector

> **Experimental: not live-validated.** The connector is tested against a fake SmartThings cloud (WireMock) with Home
> Assistant's SmartThings test fixtures (scrubbed, Apache-2.0) and synthetic capability definitions
> (`tests/ControlHome.Connectors.Tests/Fixtures/SmartThings/SOURCE.md`), not yet against a real account. Use the
> [live validation checklist](#live-validation-checklist) to change that.

Connector key `smartthings` (`src/ControlHome.Connectors.SmartThings`). It talks to Samsung's cloud REST API
(`https://api.smartthings.com/v1`): **one integration per SmartThings location**, signed in with **your household's own
OAuth-In app**. SmartThings cannot push events to a LAN-only server (webhooks need a public HTTPS address), so the
connector **polls** — every device every 30 s, and a device you just commanded every 5 s for 30 s.

- **Mibo Smart** (Intelbras) cameras, locks, lights, outlets and switches are "Works with SmartThings": add them to the
  SmartThings app and they come in through this connector, with no Intelbras-specific code. (Mibo Zigbee sensors such as
  the MSM 1001 work better re-paired to Zigbee2MQTT: see `docs/connectors/mqtt.md`.)
- **Your own app, your own account.** ControlHome never ships or hosts a shared client id or secret: each household
  creates its own app under its own Samsung account. That keeps the integration (and any bill) yours.
- **Paid Personal Plan.** Samsung announced paid API tiers from October 2026 (a US$ 4.99/month Personal Plan for
  non-commercial developers) and said creating or managing API apps with the CLI will need a paid tier. As of 2026-10
  billing was reported as not enforced yet and Brazil pricing was unknown. If SmartThings answers that a plan is
  required, the integration shows **Plan required** (it never guesses) and tries again every 30 minutes.

## Create your own SmartThings app

You do this once, on any computer with [Node.js](https://nodejs.org) 20 or later. Samsung's Developer Workspace cannot
create OAuth-In apps ("coming soon"), so the SmartThings CLI is the way.

1. Run `npx @smartthings/cli apps:create`. The first time, the CLI opens a browser to sign in with your Samsung account
   (the one your SmartThings devices are in).
2. When it asks what kind of app, choose **`OAuth-In App`**.
3. Give it a display name (for example `ControlHome`) and a short description. The icon and the target URL can stay
   empty (the target URL is only for webhooks, which ControlHome does not use).
4. Select the scopes **`r:locations:*`**, **`r:devices:*`** and **`x:devices:*`** (read locations and rooms, read devices,
   send commands). Nothing else is needed today.
5. Add the redirect URI **`https://httpbin.org/get`** — or an HTTPS page you control (see
   [the redirect URL](#the-redirect-url-and-your-privacy)). It must match exactly what you enter in ControlHome.
6. The CLI prints the **OAuth Client Id** and the **OAuth Client Secret** once: copy both now.

Useful later: `npx @smartthings/cli apps` lists your apps; `npx @smartthings/cli apps:oauth:update <appId>` changes the
scopes or redirect URIs; `npx @smartthings/cli apps:oauth:generate <appId>` makes a new client secret (then sign in again
in ControlHome with it). The wizard shows these instructions from data
(`src/ControlHome.Connectors.SmartThings/Resources/setup-instructions.json`), so they can follow Samsung's changes.

## The redirect URL and your privacy

After you allow access, SmartThings sends your browser to the redirect URL with a **one-time authorization code** (and
ControlHome's `state`) in the address. You then copy that address back into ControlHome.

- With the default **`https://httpbin.org/get`**, the third-party test site httpbin.org receives that address (it shows
  it back to you as JSON). The code alone is useless: exchanging it needs your client secret, which never leaves
  ControlHome, and it expires within minutes and works once. Still, httpbin.org sees it, plus your browser's IP and user
  agent.
- Prefer not to? Register **any HTTPS page you control** (a static page on your own domain is enough; it does not have to
  work, only to be reachable so you can copy its address). Redirects to `localhost` or LAN addresses have been reported
  to fail at SmartThings' sign-in page.
- ControlHome checks the pasted `state` against the one it generated for that sign-in (CSRF protection): an address from
  another sign-in, or someone else's, is refused, and after five refused pastes the sign-in has to start over.

## Add the integration in ControlHome

*Integrations → Add → Samsung SmartThings*, then:

1. **Your own SmartThings app (recommended)**. The instructions above are shown with a link to this page.
2. Fill in the form:

   | Field | Value | Notes |
   | --- | --- | --- |
   | OAuth client id | from the CLI | |
   | OAuth client secret | from the CLI | A **secret**: stored encrypted, never returned by the API, never logged. |
   | Redirect URL | `https://httpbin.org/get` | Exactly the redirect URI of your app. |
   | OAuth URLs | `documented` | `documented`: `api.smartthings.com/v1/oauth/authorize` and `…/v1/oauth/token` (Samsung's docs). `homeAssistant`: `api.smartthings.com/oauth/authorize` and `auth-global.api.smartthings.com/oauth/token` (what Home Assistant uses). `custom`: the two advanced fields (HTTPS on `smartthings.com` only — your client secret goes there). Sources disagree on these URLs; if signing in fails, try the other preset. |

3. **Open the SmartThings page**, sign in, choose the **location** for this integration and allow access. Your browser
   lands on the redirect URL: copy the whole address from the address bar (on httpbin.org you can also copy the page's
   JSON text) and paste it into ControlHome.
4. ControlHome exchanges the code (HTTP Basic client authentication) and reads your locations. With several, it asks
   which one; each further location is **another integration**.

To sign in again (expired or revoked authorization, a new client secret): open the integration and choose *Sign in
again*. Devices, rooms and automations are kept. A blank client secret keeps the stored one while the client id stays
the same. Signing in to a different location is refused (add it as a new integration instead).

## Development mode: personal access token

For trying things out only: choose *Personal access token* and paste a token from
[account.smartthings.com/tokens](https://account.smartthings.com/tokens) with the scopes `r:locations:*`,
`r:devices:*` and `x:devices:*`. **Tokens created since 30 Dec 2024 expire 24 hours after they were created**; the
integration then shows *Needs re-authorization*. While it runs, its status says so ("Development mode: … expires …").

## How it works

**Tokens.** Access tokens live about 24 h; refresh tokens are **single-use** and rotate on every refresh (unused, they die
after about 30 days). ControlHome refreshes when less than 3 h are left and at least once a week. Refreshes are
single-flight (one at a time; callers that waited reuse the result) and the new access + refresh token pair is saved as
**one** secret before it is used, so a crash can never leave a spent refresh token behind. A refresh request is sent
exactly once (never retried) and is not abandoned half-way when a caller gives up. Only one process runs connectors at a
time (the single-active-runtime lock), so two instances never refresh the same token. A rejected token (HTTP 401) is
refreshed once and the read repeated; an `invalid_grant` (spent, expired or revoked: also what happens when you remove
the app in the SmartThings app — a LAN-only server never gets that event) shows **Needs re-authorization**.

**Polling and rate limits.**

- Every 30 s (*Polling interval*, 30–120 s): `GET /v1/devices?locationId=…&includeStatus=true&includeHealth=true&includeRestricted=true`,
  following `_links.next` (only when the link points at the API itself; a bearer token is never sent anywhere else).
  A complete listing is the full inventory: a device that disappears is **orphaned** (kept with its name, room and
  history, never deleted); a listing that could not be read completely only adds and updates.
- The `X-RateLimit-Limit/Remaining/Reset` headers drive a budget per kind of call. SmartThings' documents disagree on the
  unit of `X-RateLimit-Reset` (milliseconds in the API reference, seconds on the rate-limit pages): values above 1000 are
  read as milliseconds, smaller ones as seconds (unless that bucket has already shown milliseconds), epoch timestamps as
  absolute, and every wait is capped at 15 minutes. With less than half of the budget left the poll slows to 60 s; when
  it is used up, the poll waits for the reset instead of earning a 429. A 429 blocks that kind of call until
  `Retry-After` (or the reset) and the integration is *Degraded* meanwhile.
- After a command the device's status (`GET /v1/devices/{id}/status`) is read every 5 s for 30 s. Status reads and
  commands together stay within **12 per minute per device** (a sliding window; two are kept free for commands).
- Rooms (`GET /v1/locations/{id}/rooms`, limited to 50 calls an hour) are read at start and every 6 hours, or again
  (at most every 15 minutes) when a device names a room nobody knows; they only suggest rooms when devices are imported.
- Idempotent GETs are retried twice on network errors, timeouts and 5xx (exponential back-off with jitter); nothing else
  is retried. While SmartThings cannot be reached the integration is *Degraded*, and after three failed polls in a row
  every device is *Unknown*.

**Commands.** `POST /v1/devices/{id}/commands` with the `component` field, at most 10 commands per request (a bigger
batch goes out in several requests). `ACCEPTED` (or `COMPLETED`) only means SmartThings queued the command: ControlHome
keeps it *pending* until the polled state confirms it, and a `FAILED` result is reported as refused. Commands are
**at most once**: never retried, a repeated idempotency key within 10 minutes is a duplicate (also after a timeout), and
an unlock without an idempotency key is refused. Unlocking needs the step-up PIN like every safety-critical command.

**Devices.** Each enabled component becomes an endpoint (`main`, `freezer`, `cooler`, …); components and capabilities
listed in `custom.disabledComponents` / `custom.disabledCapabilities` are hidden.

| SmartThings capability | ControlHome capability |
| --- | --- |
| `switch` | switch (the power of a TV or speaker is the media player's; an AC keeps its switch too) |
| `switchLevel` | level (with the device's `levelRange`) |
| `colorControl`, `colorTemperature` | color (SmartThings hue 0–100 → 0–360°; the device's colour temperature range) |
| `airConditionerMode` + `thermostatCoolingSetpoint` + `airConditionerFanMode`, or `thermostatMode` + setpoints | thermostat: modes the device supports (`wind`/`fanOnly` → `fan`; the AC's switch is `off`), the setpoint range (`custom.thermostatSetpointControl`), fan modes; °F converted |
| `temperatureMeasurement`, `relativeHumidityMeasurement`, `contactSensor`, `motionSensor`, `presenceSensor`, `occupancySensor`, `waterSensor`, `smokeDetector`, `battery` | temperature (°C), humidity, contact, motion, presence, occupancy, water leak, smoke, battery |
| `dustSensor`/`fineDustSensor`, `carbonDioxideMeasurement`, `tvocMeasurement` | air quality (PM2.5, CO₂, VOC in ppb); PM10 stays generic |
| `lock` | lock (`unlock` is safety-critical); `lockCodes` is never exposed (it holds people's names) |
| `mediaPlayback`, `audioVolume`, `audioMute`, `mediaInputSource`/`samsungvd.mediaInputSource`, `mediaTrackControl`, `custom.launchapp` | media player (input sources as allowed values); `tvChannel` stays generic |
| `washerOperatingState`/`dryerOperatingState`/`dishwasherOperatingState`, `samsungce.*OperatingState`, `remoteControlStatus` | appliance cycle: state, phase, remaining minutes; start/pause/stop only while **remote control** is enabled on the machine |
| `windowShade` (+ `windowShadeLevel`) | cover (blind) |
| `doorControl`, `garageDoorControl` | cover of class **garage** (opening is safety-critical) |
| `button` | button press events (`pushed` → single, `double`, `held` → long), from the timestamp of the last press |
| `powerMeter`, `energyMeter` | power (W), energy (kWh → Wh) |
| `powerConsumptionReport` | its cumulative counter → energy (Wh) and its power → power (W) where the device has no meter (EHS heat pumps report kW: × 1000, like Home Assistant) |
| anything else | the endpoint's `generic` capability, built from the capability's definition (`GET /v1/capabilities/{id}/{version}`, cached for 3 days): enums, ranges (a sibling `…Range` narrows its value: a fridge compartment's setpoint), units converted; a setter or a value → command table makes it writable. Option lists (`supported…`, `available…`) are not attributes. Capabilities that may lock or open something (lock, door, garage, gate, security, alarm, valve, siren, keypad, code) get **no** generic commands. Without a definition (custom namespaces) the attributes are inferred from their values, read-only. |

Plumbing and diagnostics (`ocf`, `healthCheck`, `refresh`, `execute`, firmware and software update, device identification,
…) are not shown. The firmware, model and manufacturer come from them; a Samsung appliance's serial number becomes a
hardware connection (to spot the same device in another integration).

## Limitations

- **Polling only**: no webhooks (they need a public HTTPS target with SmartThings' signature handshake; a possible
  future option for installs behind a tunnel) and no SSE (reserved for partner apps such as Home Assistant's).
- **Energy windows**: `powerConsumptionReport` windows (15–30 min of `deltaEnergy`) are parsed but not yet stored as
  hourly vendor windows; the cumulative counter feeds the energy history meanwhile. TVs that list the report under
  `custom.disabledCapabilities` (while still sending real values) have no per-device "show anyway" override yet.
- Capability definitions are cached in memory (a restart fetches them again, a few per poll); until a definition arrives,
  that capability's generic attributes are left out (never guessed and changed later).
- Scenes are not exposed yet (they will become automation actions); they will need the `r:scenes:*` and `x:scenes:*`
  scopes (add them with `apps:oauth:update`, then sign in again).
- Generic controls of lock-, door- or valve-like vendor capabilities are read-only by design.
- If ControlHome is stopped exactly while it refreshes a token, the new pair may not be saved; the next start then needs
  *Sign in again*.

## Troubleshooting

- *SmartThings refused the authorization* / *Needs re-authorization*: the refresh token was spent, expired (30 days
  without ControlHome running) or revoked (the app was removed in the SmartThings app), or the client secret changed.
  Sign in again. If it happens again right after signing in, sign in once more (re-authorizing an already authorized
  location can hand out the token another refresh had just replaced).
- Sign-in page shows *403 Access forbidden* or *An unexpected error occurred*: check the redirect URI (exactly as
  registered, HTTPS, not localhost) and try the other OAuth URL preset.
- *That address does not belong to this sign-in*: you pasted an address from an older attempt; use the button on the
  current step and paste the new address.
- *Plan required*: SmartThings asked for its paid plan. Subscribe in your Samsung account; ControlHome retries every 30
  minutes (or disable and re-enable the integration).
- *Rate limit … waiting*: the integration waits for SmartThings' window to reset; reduce the polling frequency if it
  happens often (*Polling interval* in the advanced settings).
- An appliance ignores start/pause/stop: enable **Remote control** on the machine (its panel) — the control stays
  disabled in ControlHome until SmartThings reports it on.
- A device's extra settings are missing: its capability definitions are fetched a few per poll; wait a few minutes.

## Live validation checklist

Tick each item with a real account (record the date and firmware where relevant):

1. [ ] `npx @smartthings/cli apps:create` creates an OAuth-In app with the three scopes and `https://httpbin.org/get` as
   redirect URI; the client id/secret are printed.
2. [ ] Sign-in with the `documented` preset works (else: which preset works — `homeAssistant` or a custom
   `account.smartthings.com` authorize URL); the pasted httpbin address (address bar and JSON text) is accepted.
3. [ ] A pasted address from an earlier sign-in is refused (state mismatch).
4. [ ] The token exchange returns `expires_in` ≈ 86 399 and an `installed_app_id`; the location list shows only the
   authorized location (one integration per location).
5. [ ] All devices import with sensible names, rooms, classes and endpoints (fridge compartments, washer `hca.main`);
   disabled components/capabilities are hidden.
6. [ ] States match the SmartThings app: lights (level, colour), plugs (W/Wh), AC (mode, setpoint, fan, room
   temperature), fridge compartments (°C), washer (state, remaining time, remote control), TV (power, volume, source),
   locks, sensors.
7. [ ] Commands: light on/off/level/colour, AC mode/setpoint/fan (and off through its switch), TV power/volume/source,
   washer start/pause (with remote control on and off), lock and **unlock with the PIN step-up**; each is confirmed by the
   hot poll within 30 s.
8. [ ] Rate-limit headers: note the actual `X-RateLimit-Reset` unit and values for `GET /devices` and device status; no
   429 during a day of normal use (diagnostics: API call counter).
9. [ ] Token refresh: after ~21 h the access token is refreshed once, the integration keeps working, and a restart after
   the refresh still works (the rotated pair was saved).
10. [ ] Removing the app in the SmartThings app (or `apps:oauth:generate`) leads to *Needs re-authorization*; *Sign in
    again* keeps every device, room and automation.
11. [ ] Personal access token mode works and shows the 24-hour warning; after expiry it shows *Needs re-authorization*.
12. [ ] What a paid-plan rejection looks like, if Samsung enforces it (status code and body) — adjust the plan detection
    if needed.
13. [ ] Mibo Smart devices added to SmartThings appear and can be controlled.
14. [ ] `powerConsumptionReport` windows of a Samsung appliance (start/end/`deltaEnergy`) look plausible against its
    meter (for the energy-window wiring).
