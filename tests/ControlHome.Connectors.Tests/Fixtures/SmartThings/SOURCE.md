# SmartThings connector fixtures — provenance

## `devices/`, `device_status/`, `locations.json`, `rooms.json` — Home Assistant (Apache-2.0)

Copied from Home Assistant's SmartThings integration tests,
[`home-assistant/core` `tests/components/smartthings/fixtures/`](https://github.com/home-assistant/core/tree/dev/tests/components/smartthings/fixtures)
at commit `eeb462fdfadca195d8936b6b42ec9de55adb9070`, licensed under the
[Apache License 2.0](https://github.com/home-assistant/core/blob/dev/LICENSE.md) (Copyright the Home Assistant
contributors). Same file names and structure: `devices/<name>.json` is a `GET /v1/devices` page (`{"items":[…]}`),
`device_status/<name>.json` a `GET /v1/devices/{id}/status` answer.

| Fixture | Device |
| --- | --- |
| `hue_rgbw_color_bulb` | Philips Hue colour bulb (switch, level, colour, colour temperature; vendor capabilities) |
| `smart_plug` | Zigbee smart plug (switch) |
| `da_ac_rac_000001` | Samsung room air conditioner (two components, one disabled; many disabled capabilities; `powerConsumptionReport`) |
| `da_wm_wm_000001` | Samsung washer (`washerOperatingState`, `samsungce.*`, `remoteControlStatus`, component `hca.main`) |
| `da_ref_normal_000001` | Samsung refrigerator (compartment components, °F, disabled components, `powerConsumptionReport`) |
| `vd_stv_2017_k` | Samsung TV 2017 (media capabilities, input sources) |
| `yale_push_button_deadbolt_lock` | Z-Wave deadbolt (lock, battery, `lockCodes`) |
| `multipurpose_sensor` | Zigbee multipurpose sensor (contact, °F temperature, acceleration, battery) |
| `aeotec_ms6` | Z-Wave multisensor (motion, humidity, illuminance, …; has a parent device) |
| `ecobee_thermostat_offline` | Cloud thermostat reported offline (all values null) |

**Scrubbed** before committing (with a one-off script; neither it nor the raw downloads were committed): every UUID
(device, location, room, owner, hub, profile ids, `ocf.di`, the TV's `udn`) replaced by a synthetic
`00000000-0000-4000-8000-xxxxxxxxxxxx` (consistently across files, so devices still reference their rooms); 32-hex
identifiers, Zigbee EUIs and network ids, Samsung `otnDUID`/`duid` and serial numbers replaced by synthetic values; the
TV's MAC addresses replaced by `02:00:00:00:00:0x`; the names of the people in the lock's `lockCodes` replaced by
`Code 1…5`; personal device labels (a person's name, a home server) replaced by neutral ones. Values, units, timestamps
and structure are unchanged. `device_health.json` was not kept.

In the tests, a `GET /v1/devices?includeStatus=true&includeHealth=true` item is composed from a device and its status
(`StFixtures.ListingItem`): each capability's status is embedded as `components[].capabilities[].status` (the Swagger
spec's `DeviceCapabilityReference`) and a `healthState` is added — that composition is synthetic.

## `capabilities/` — synthetic

Hand-written capability definitions shaped after the SmartThings API's `Capability` schema
(`swagger.api.smartthings.com/public/st-api.yml`: `attributes.<name>.schema.properties.value`, `setter`,
`enumCommands`, `commands.<name>.arguments`), for the capabilities of the fixtures above that the tests map to the
`generic` capability: `custom.washerSpinLevel`, `samsungce.washerDelayEnd`, `samsungce.kidsLock`, `hca.washerMode`,
`thermostatCoolingSetpoint`, `audioVolume`, `samsungce.sabbathMode`. They are not copies of Samsung's definitions;
enum values follow the fixtures' `supported…` lists.

## Everything else

OAuth answers, tokens (`access-token-0001`, …), client ids and secrets, location names and rate-limit headers in the
tests are synthetic; no value comes from a real account.
