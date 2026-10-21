# LG ThinQ connector fixtures — provenance

No payload here was captured from a real LG account or appliance. Device ids are made up (`test-<kind>-000n`), and no
token, client id, certificate, serial number, MAC or IP address appears anywhere.

| File | Source | Notes |
| --- | --- | --- |
| `ac.profile.json`, `ac.status.json`, `ac.energy-profile.json` | Home Assistant `tests/components/lg_thinq/fixtures/air_conditioner/` ([home-assistant/core](https://github.com/home-assistant/core), Apache-2.0) | Copied unchanged (the device id lives in `devices.json`). Includes the `temperature` resource and the `temperatureInUnits` list (°C and °F entries). |
| `washer.profile.json`, `washer.status.json` | Home Assistant `fixtures/washer/` (Apache-2.0) | Copied unchanged: a profile whose `property` is a list of locations (`MAIN`), a constant property (`detergentSetting`). |
| `washtower-dryer.profile.json`, `washtower-dryer.status.json` | Home Assistant `fixtures/washtower_dryer/` (Apache-2.0) | Copied unchanged (a `DEVICE_WASHTOWER_DRYER`, a separate device linked by `groupId`). |
| `air-purifier.profile.json`, `air-purifier.status.json` | Home Assistant `fixtures/air_purifier/` (Apache-2.0) | Copied unchanged. |
| `dehumidifier.profile.json`, `dehumidifier.status.json` | Home Assistant `fixtures/dehumidifier/` (Apache-2.0) | Copied unchanged. |
| `kimchi.profile.json`, `kimchi.status.json` | Home Assistant `fixtures/kimchi_refrigerator/` (Apache-2.0) | Copied unchanged: compartments as a `temperature` list with `locationName`. |
| `refrigerator.profile.json`, `refrigerator.status.json` | Synthetic, after pythinqconnect's `RefrigeratorProfile` ([thinq-connect/pythinqconnect](https://github.com/thinq-connect/pythinqconnect) `devices/refrigerator.py`, Apache-2.0) | `doorStatus` and `temperatureInUnits` as lists of locations (`MAIN`, `FRIDGE`, `FREEZER`) with `targetTemperatureC`/`F`, plus `powerSave`, `refrigeration`, `waterFilterInfo`. Values invented. |
| `dishwasher.profile.json`, `dishwasher.status.json` | Synthetic, after pythinqconnect `devices/dish_washer.py` | Resource and property names from the SDK's profile map; enum values invented within the SDK's documented sets. |
| `robot-cleaner.profile.json`, `robot-cleaner.status.json` | Synthetic, after pythinqconnect `devices/robot_cleaner.py` | `runState`, `robotCleanerJobMode`, `operation.cleanOperationMode`, `battery.level`/`percent`. |
| `washtower.profile.json`, `washtower.status.json` | Synthetic, after pythinqconnect `devices/washtower.py` (`WashTowerProfile`: `washer` and `dryer` sub-profiles; status and reports keyed `washer` / `dryer`) | One `DEVICE_WASHTOWER` device id with two sub-devices. |
| `devices.json` | Synthetic, shaped like `GET devices` in pythinqconnect `thinq_api.py` / Home Assistant `device.json` fixtures (`deviceId`, `deviceInfo.deviceType`, `modelName`, `alias`, `reportable`, `groupId`) | Model names of the Home Assistant fixtures where those are used; others are placeholders. |

REST answers in the tests (`{messageId, timestamp, response}` and `{error: {code, message}}`), MQTT messages
(`{deviceId, deviceType, pushType: DEVICE_STATUS|DEVICE_PUSH, report|pushCode}`) and energy usage answers
(`{result: {dataList: [{usedDate, energyUsage}]}}`) are written inline in the tests after the same SDK sources and
Home Assistant's `lg_thinq` integration (`mqtt.py`, `sensor.py`). The energy answer's `usedDate` field name is an
assumption (the SDKs only sum `dataList`); the parser also accepts `date` and a single undated entry for a one-period
query — see `docs/connectors/lgthinq.md` (live validation checklist).

The MQTT integration test runs `eclipse-mosquitto:2.1.2-alpine` (Testcontainers) with a CA, server and client
certificates generated in memory for each test run (RSA, never written to the repository), `require_certificate true`
and `use_identity_as_username true`.
