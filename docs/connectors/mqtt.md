# MQTT connector (Zigbee2MQTT, Home Assistant discovery, Tasmota)

> **Experimental: not live-validated.** The connector is tested against a real Mosquitto 2.1.2 broker with synthetic
> payloads shaped after the public Zigbee2MQTT, Home Assistant and Tasmota documentation
> (`tests/ControlHome.Connectors.Tests/Fixtures/Mqtt/SOURCE.md`), not yet against the owner's devices.

Connector key `mqtt` (`src/ControlHome.Connectors.Mqtt`). One integration holds **one broker connection** and runs up
to three adapters over it:

| Adapter | Listens on | Turns into devices |
| --- | --- | --- |
| Zigbee2MQTT (native) | `<base>/#` (default base `zigbee2mqtt`) | every interviewed, enabled device of the retained `<base>/bridge/devices`, plus the bridge itself |
| Home Assistant MQTT discovery (intake only) | `<prefix>/#` (default prefix `homeassistant`) | each discovered *device* (configs grouped by their `device` identifiers) |
| Tasmota native discovery | `tasmota/discovery/#` | each Tasmota device (`tasmota/discovery/<MAC>/config` + `/sensors`) |

Leave the Zigbee2MQTT base topic or the discovery prefix empty to switch that adapter off; untick *Tasmota discovery*
to switch Tasmota off.

## Setting it up on Umbrel (Mosquitto + Zigbee2MQTT apps)

ControlHome reuses Umbrel's own **Mosquitto** and **Zigbee2MQTT** apps instead of bundling its own broker.

1. **Install the Mosquitto app.** It runs `eclipse-mosquitto:2.1.2-alpine` and publishes the broker on the Umbrel's
   **host port 1883** (the app's web page on port 9021 is only an info page). Zigbee2MQTT reaches the same broker
   inside Docker as `mosquitto_broker_1:1883`.
2. **Install the Zigbee2MQTT app** (2.14.x) and finish its onboarding (adapter, channel). It uses the default base
   topic `zigbee2mqtt`. In its settings, turn on **Availability** so ControlHome can show devices as offline (without
   it a device counts as online whenever it reports).
3. **Credentials.** Umbrel's Mosquitto app ships with `allow_anonymous true`, so the user name and password can stay
   empty. Recommended for a real home: switch on authentication.
   - Create a password file inside the broker container (run on the Umbrel, `-c` only for the first user):
     `docker exec -it mosquitto_broker_1 mosquitto_passwd -c /mosquitto/config/passwd controlhome`, then add a user
     for Zigbee2MQTT the same way without `-c`.
   - In the app's config (`app-data/mosquitto/data/config/mosquitto.conf` in the Umbrel home folder) set
     `allow_anonymous false` and `password_file /mosquitto/config/passwd`, then restart the Mosquitto app.
   - Give Zigbee2MQTT its user (its *Settings → MQTT* page, or `mqtt.user` / `mqtt.password` in its
     `configuration.yaml`) and restart it.
4. **Add the integration in ControlHome** (*Integrations → Add → MQTT*):

   | Field | Umbrel value | Notes |
   | --- | --- | --- |
   | Broker host | `127.0.0.1` | ControlHome runs with host networking on Umbrel. From another machine use the Umbrel's LAN IP (inside containers prefer an IP to `umbrel.local`). |
   | Port | `1883` | `8883` is usual with TLS. |
   | Use TLS | off | Umbrel's Mosquitto has no TLS listener. With TLS on, the broker's certificate must be trusted by the server. |
   | User name / Password | `controlhome` / the password you set, or empty | The password is a **secret**: stored encrypted, never returned by the API, never logged. |
   | Client id | empty | Defaults to `controlhome-<integration id>`; must be unique on the broker. |
   | Home Assistant discovery prefix | `homeassistant` | Empty switches the HA-discovery intake off. |
   | Zigbee2MQTT base topic | `zigbee2mqtt` | Empty switches the Zigbee2MQTT adapter off. |
   | Tasmota discovery | on | Listens on `tasmota/discovery/#`. |

   The setup flow **tests the connection** (with a throw-away client id) before saving: an unreachable broker comes
   back as an error on *Broker host*, rejected credentials as an error on *Password*.
5. **Pair Zigbee devices from ControlHome:** open the integration's flow again and choose *Pair Zigbee devices*. It
   publishes `zigbee2mqtt/bridge/request/permit_join` with `{"time": <seconds>}` (1–254 s, default 254) and shows a
   progress step that lists the devices that joined (`bridge/event` `device_joined` / `device_interview`) until the
   window closes or you stop it (`{"time": 0}`). The bridge device also has a `permitJoin(seconds)` command.

The Mosquitto app runs without persistence: a broker restart forgets retained messages. That is fine: Zigbee2MQTT
re-publishes `bridge/devices` and device states when it reconnects, Tasmota re-publishes its discovery on every
connection, and ControlHome publishes the Home Assistant birth message (`homeassistant/status` = `online`) after every
(re)connection so HA-discovery devices announce themselves again. A broker restart never orphans devices.

## Connection behaviour

- MQTTnet 5.2 client (MQTT 3.1.1, clean session, QoS 1, 30 s keep-alive). MQTTnet v5 has no managed client, so the
  runtime owns its **reconnect loop**: exponential back-off 1 s → 60 s (doubling) with ±20 % jitter, reset after a
  successful connection.
- After every (re)connection it **re-subscribes** the adapters' wildcard filters plus every device topic outside them
  (e.g. an HA device's `kitchen/climate/state`), then asks devices without an availability topic for their state.
- While the broker is unreachable the integration is `Degraded` (message names `host:port`, never credentials) and every
  device is `Unknown`. Rejected credentials (`bad user name or password`, `not authorized`) set `NeedsReauth`; the loop
  keeps retrying, so fixing the broker's users is enough.
- Commands are at-most-once: an idempotency key seen within 10 min is rejected as `duplicate`, a safety-critical batch
  without a key as `idempotency_required`, and nothing is published (`unavailable`) while disconnected. Commands are
  `Accepted` (confirmed by the next state message) unless the entity has no state topic (optimistic: the assumed state
  is reported and the command is `Completed`) or is a button press.

## Zigbee2MQTT

| Topic | Meaning |
| --- | --- |
| `<base>/bridge/devices` (retained) | inventory: one device per entry, `ieee_address` is the external id; devices missing from a newer list are **orphaned** (never deleted). The coordinator, disabled devices and devices still interviewing are skipped. |
| `<base>/bridge/info`, `<base>/bridge/state` | the bridge device (parent of every Zigbee device): version, `permitJoin`, availability |
| `<base>/bridge/event` `device_renamed` | the device moves to its new friendly name at once (a new `bridge/devices` follows) |
| `<base>/<friendly_name>` | state (JSON) |
| `<base>/<friendly_name>/availability` | `{"state":"online"}` / `{"state":"offline"}` (or plain `online` / `offline`) |
| `<base>/<friendly_name>/set` | commands: one merged JSON object per command batch |
| `<base>/<friendly_name>/get` | gettable properties are asked for after discovery and after a reconnect (mains-powered devices only; battery devices sleep) |

Capabilities come from `definition.exposes` and their access bits (1 published, 2 settable, 4 gettable):

| Expose | Capability |
| --- | --- |
| `light` (`state`, `brightness`, `color_temp`, `color_xy` / `color_hs`) | switch, level (0–254 → %), color (mired → K with the device's range; xy ↔ hue/saturation) |
| `switch` | switch (outlet device class when the description says plug/socket/outlet) |
| `cover` (`state`, `position`) | cover (`garage`/`gate` device class from the description makes `open`/`setPosition` safety-critical) |
| `lock` (`state`, `lock_state`) | lock (`unlock` is safety-critical) |
| `climate` (`system_mode`, `occupied_heating_setpoint`/`current_heating_setpoint`/`occupied_cooling_setpoint`, `local_temperature`, `fan_mode`) | thermostat, constrained to the device's modes and setpoint range |
| `temperature`, `humidity`, `battery`, `power`, `energy` (kWh → Wh), `co2`, `pm25`, `voc` | their catalogue capabilities |
| `occupancy`, `presence`, `contact` (`true` = closed → `open: false`), `water_leak`, `smoke` | occupancy, presence, contact, waterLeak, smoke |
| `illuminance`, `voltage`, `current` | generic measurements (lx, V, A; no catalogue capability yet) |
| `action` (enum) | `button.press` events (`single`, `double`, `hold` → `long`); other values become events of a generic momentary `action` |
| anything else (numeric, binary, enum, text, composite, list, unknown) | the endpoint's `generic` capability: settable ones get a `set<Name>` command, composite/list/unknown are read-only JSON |

An expose's `endpoint` (e.g. `l1`, `left`) becomes an endpoint key; a second instance of a capability goes to its own
endpoint.

## Home Assistant MQTT discovery (intake)

- **Topics:** device-based `<prefix>/device/<object_id>/config` (`dev`, `o` and a `cmps` map whose entries carry `p`)
  and single-component `<prefix>/<component>/[<node_id>/]<object_id>/config`. Abbreviations are expanded (also in
  `dev`, `o` and `availability`), and `~` is applied to every `*_topic` value that starts or ends with it.
- **Grouping:** configs belong to the device of their first `device.identifiers` (else the first `connections` pair,
  else the entity), external id `ha:<identifier>`. Configs published by Zigbee2MQTT's own HA integration are skipped
  while the native Zigbee2MQTT adapter is on (no duplicates).
- **Components:** `switch` → switch; `light` (default and `json` schema) → switch/level/color; `sensor` and
  `binary_sensor` by `device_class` → temperature, humidity, power, energy, battery, air quality, contact, motion,
  occupancy, presence, smoke, water leak (others → generic); `cover` → cover; `lock` → lock; `climate` → thermostat;
  `button` → a generic `press<Name>` command. Other components are ignored.
- **Templates:** `value_template` and friends support exactly one `{{ … }}` expression over `value` or `value_json`
  with `.key`, `["key"]`, `['key']`, `[0]` accessors and the filters `float`, `float(default)`, `int`, `int(default)`,
  `round`, `round(n)`, `string`, `lower`, `upper`, `trim`. Command templates support literal text with
  `{{ value }}` placeholders (`{{ position }}` for `set_position_template`). Anything else is **not a crash**: the
  attribute (or command) is marked unsupported in the diagnostic `unsupportedTemplates` attribute, and a sensor keeps
  its raw payload in a diagnostic text attribute.
- **Removal:** a retained **empty** config removes its entities; a device left without entities is **orphaned**.
- **Availability:** `availability_topic` / `availability` with `payload_available` / `payload_not_available`; a device
  without one counts as online once it reports.

## Tasmota (native discovery)

Tasmota's own discovery (`SetOption19 0`, the default in current firmware; the HA-style `SetOption19 1` is deprecated)
publishes `tasmota/discovery/<MAC>/config` and `/sensors` (retained). External id `tasmota:<MAC>`; topics follow the
device's full topic (`ft`, e.g. `%prefix%/%topic%/`) and prefixes (`tp`).

- **Relays** (`rl` 1 = relay, 2 = light) → switch (+ level for a dimmable light): one relay on `main`, several on
  `relay1…n` (named after `fn`). State from `stat/<t>/RESULT`, `stat/<t>/POWER<n>`, `tele/<t>/STATE`; commands to
  `cmnd/<t>/POWER<n>` (`ON`/`OFF`/`TOGGLE`) and `cmnd/<t>/Dimmer`.
- **Energy** (`tele/<t>/SENSOR` `ENERGY`) → power (W), energy (`Total` kWh → Wh), generic `energyToday`,
  `energyYesterday`, voltage, current, apparent/reactive power and power factor.
- **Sensors** → temperature (°F converted), humidity, air quality and generic measurements; a second sensor of the same
  kind gets its own endpoint (e.g. `ds18b20`).
- **Availability** from the LWT `tele/<t>/LWT` (`Online` / `Offline`). After discovery the device is asked for its state
  (`cmnd/<t>/STATE`, `cmnd/<t>/STATUS 10`).
- A retained empty `config` orphans the device.

## Troubleshooting

- *Cannot reach the broker at host:port*: check the host/port from the ControlHome server (inside a container use an
  IP), and that the broker listens on the LAN (Mosquitto 2.x binds to loopback only without a `listener` line).
- *Rejected the user name or password / not authorized*: the broker's password file or ACLs; the integration retries by
  itself once fixed.
- A Home Assistant device shows `unsupportedTemplates`: its templates use Jinja features outside the subset above; the
  listed attributes stay empty or keep the raw payload.
