# MQTT connector fixtures — provenance

Every file here is **synthetic**: hand-written for these tests, shaped after the public documentation of each
project. No payload was captured from a real home or a real device; addresses, IEEE/MAC ids, serials and IPs are made up
(MACs and IEEE ids use test-only patterns such as `0x00158d00000000xx`, `DC4F22A1B2C3`).

| File | Shape taken from | Notes |
| --- | --- | --- |
| `z2m-bridge-devices.json` | Zigbee2MQTT docs — [MQTT topics and messages](https://www.zigbee2mqtt.io/guide/usage/mqtt_topics_and_messages.html) (`bridge/devices` entry fields) and [Exposes](https://www.zigbee2mqtt.io/guide/usage/exposes.html) (expose types, `access` bits 1/2/4, `value_on`/`value_off`, `endpoint`, `category`) | Model/vendor strings follow the public device pages; exposes are written per the generic/specific expose definitions (e.g. `contact` declares `value_on: false`, as Zigbee2MQTT's `e.contact()` does). Includes the coordinator, a disabled device, a device still interviewing (no definition), and unknown/composite/list exposes on purpose. |
| `z2m-bridge-info.json` | Zigbee2MQTT docs — `bridge/info` | Trimmed to the fields the connector reads (`version`, `coordinator.type`, `permit_join`). |
| `ha-discovery.json` | Home Assistant docs — [MQTT discovery](https://www.home-assistant.io/integrations/mqtt/) (device-based `<prefix>/device/<id>/config` with `dev`/`o`/`cmps`/`p`, single-component topics with optional node id, `~` base topic, abbreviations, `availability`), and the per-platform pages (light default and JSON schema, switch, cover, lock, climate, button, sensor, binary sensor) | Map of config topic → payload. The device-based example and the garden motion sensor follow the examples on the discovery page; the ESPHome-style JSON light follows ESPHome's MQTT discovery output format. One entity has an unsupported template on purpose; one config imitates Zigbee2MQTT's own HA integration (origin `Zigbee2MQTT`, identifiers `zigbee2mqtt_<ieee>`) to test de-duplication. |
| `tasmota-plug-config.json`, `tasmota-th-config.json` | Tasmota native discovery payload (`tasmota/discovery/<MAC>/config`) as produced by Tasmota's discovery driver (`xdrv_12_discovery`) and described in the [Tasmota MQTT docs](https://tasmota.github.io/docs/MQTT/) (`%prefix%/%topic%/` full topic, `cmnd`/`stat`/`tele`, LWT `Online`/`Offline`) | Keys `dn`, `fn`, `hn`, `mac`, `md`, `ofln`, `onln`, `state`, `sw`, `t`, `ft`, `tp`, `rl`, `lt_st`. The second device uses a custom full topic (`%topic%/%prefix%/`) on purpose. |
| `tasmota-plug-sensors.json`, `tasmota-th-sensors.json` | Tasmota `tasmota/discovery/<MAC>/sensors` (`{"sn": <SENSOR payload>}`) and the documented `tele/<topic>/SENSOR` / `ENERGY` fields (Power W, Total/Today/Yesterday kWh, Voltage, Current, Factor) | Values are invented. |

The integration tests run Mosquitto 2.1.2 as `eclipse-mosquitto:2.1.2-alpine` (Testcontainers; Docker Hub publishes
2.1.2 only with the `-alpine` suffix, the same image Umbrel's Mosquitto app ships) with `allow_anonymous false`, a
password file generated at start and a random password per run; no credential is stored in the repository.
