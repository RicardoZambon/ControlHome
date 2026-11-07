# Live validation of the connectors

The vendor connectors were built from vendor documentation, open-source projects and recorded fixtures. Nobody has
yet run them against real devices or real vendor accounts. Until that happens, the integrations catalogue labels them
**Experimental**.

This page is for the home owner. It says how to check a connector with your own devices and what to do with the result.
Live validation never blocks other work.

## Status

| Connector | Catalogue label | Checklist |
|---|---|---|
| Simulated | — (built in, nothing to validate) | — |
| MQTT (Zigbee2MQTT, Home Assistant discovery, Tasmota) | Experimental | [below](#mqtt-checklist) |
| LG ThinQ | Experimental | [LG ThinQ checklist](lgthinq.md#live-validation-checklist) |
| Intelbras / Dahua HTTP (cameras, recorders, access control) | Experimental | [Dahua HTTP checklist](dahuahttp.md#live-validation-checklist) |
| Samsung SmartThings | Experimental | [SmartThings checklist](smartthings.md#live-validation-checklist) |

## How to validate a connector

1. Use a test install if you can (a second ControlHome container with its own `ControlHome_Dev_*` database). The
   connectors never change anything in the vendor account except what you command, but a test install keeps your real
   home's history clean.
2. Set the log level of the connectors to `Debug` for the session:
   `Serilog__MinimumLevel__Override__ControlHome.Connectors=Debug` (secrets and tokens are redacted from every log line).
3. Work through the connector's checklist. For every item, note:
   - pass or fail;
   - the device model and firmware;
   - for a failure, what you expected and what you saw, plus the log lines around it.
4. Never paste tokens, passwords, serial numbers or camera addresses into an issue. Logs are already redacted; check
   anything else you add.

## Reporting the result

Open an issue in the [ControlHome repository](https://github.com/RicardoZambon/ControlHome/issues) titled
`Live validation: <connector>`, with the ticked checklist and your notes.

- **Everything passes:** the connector's `Experimental` feature flag is turned off in its factory, and the catalogue
  stops showing the label. Partial passes stay experimental, with the failing items fixed first.
- **A device behaves differently from the fixtures:** a scrubbed copy of the device's description becomes a new test
  fixture (its origin recorded in that folder's `SOURCE.md`), so the fix is covered by a test.

## MQTT checklist

Tick each item with your own broker and devices:

1. [ ] Connects to the Umbrel Mosquitto app with a user and password; a wrong password shows *Needs sign-in*, not a
   crash loop.
2. [ ] Zigbee2MQTT devices import with sensible names, classes and endpoints (multi-gang switches as several endpoints).
3. [ ] States follow the device within a second: switch, dimmer level, colour temperature, contact, motion, temperature,
   humidity, battery.
4. [ ] Commands: on/off, level, colour temperature; each confirmed by the device's next state message.
5. [ ] *Permit join* from ControlHome pairs a new Zigbee device, and the device then shows up without a restart.
6. [ ] A device removed in Zigbee2MQTT becomes *Orphaned* in ControlHome and keeps its history.
7. [ ] A Home Assistant discovery device (for example an ESPHome node) imports and reports its states.
8. [ ] A Tasmota plug imports through native discovery, switches, and reports W and Wh.
9. [ ] Restarting Mosquitto: ControlHome reconnects, re-subscribes and marks devices available again.
