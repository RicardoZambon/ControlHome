# LG ThinQ connector (ThinQ Connect)

> **Experimental: not live-validated.** The connector is tested against WireMock (REST, with the API's error codes and
> call limit), an in-memory broker and a real Mosquitto 2.1.2 with TLS client certificates, using Home Assistant's
> `lg_thinq` test fixtures and synthetic payloads (`tests/ControlHome.Connectors.Tests/Fixtures/LgThinQ/SOURCE.md`), not
> yet against a real LG account. Please run the [live validation checklist](#live-validation-checklist) once.

Connector key `lgthinq` (`src/ControlHome.Connectors.LgThinQ`). It talks to LG's official **ThinQ Connect** API with a
**personal access token (PAT)**: REST for the appliance list, profiles, state and control, and an outbound MQTT
connection (AWS IoT Core, mutual TLS) for real-time state and notifications. Every connection goes **out** from the
ControlHome server (HTTPS 443 to `api-<region>.lgthinq.com`, MQTT/TLS 8883 to LG's AWS IoT endpoint): nothing has to be
opened on your router, but the server needs internet access. LG TVs are not part of ThinQ Connect (a separate webOS
connector is planned).

The connector is a C# port of LG's Apache-2.0 SDK [pythinqconnect](https://github.com/thinq-connect/pythinqconnect),
cross-checked with [jsthinqconnect](https://github.com/thinq-connect/jsthinqconnect); see
`src/ControlHome.Connectors.LgThinQ/NOTICE`. The API is meant for personal, non-commercial use: every household uses its
own token.

## Setting it up

1. **Create a token.** Open <https://connect-pat.lgthinq.com> and sign in with the same LG account you use in the
   LG ThinQ app. Create a new token and tick these scopes: *view all devices*, *view all device statuses*, *device
   control*, *event subscription*, *push notification* and *energy consumption inquiry*. A missing scope hides the
   matching appliances or features. LG shows the token **only once**: copy it right away.
2. **Add the integration** in ControlHome (*Integrations → Add → LG ThinQ*):

   | Field | Value | Notes |
   | --- | --- | --- |
   | Name | e.g. `LG ThinQ` | Shown in ControlHome only. |
   | Personal access token | the token from step 1 | A **secret**: stored encrypted (Data Protection), never shown again, never logged. |
   | Country of the LG account | `BR` (default) | The country the **LG account was created in**, not where you are. It picks the API region (`BR` and the Americas → `https://api-aic.lgthinq.com`, Asia/Pacific → `kic`, Europe/Africa/Middle East → `eic`). |
   | *Advanced:* Energy history | on | Fetch daily energy once a night (see [Energy](#energy)). |
   | *Advanced:* Time zone of the energy days | empty | IANA zone (e.g. `America/Sao_Paulo`). Empty = the country's main zone (BR → `America/Sao_Paulo`), else the server's. |
   | *Advanced:* API key (x-api-key) | empty | Empty = the public key of LG's SDK. Change it only if LG publishes a new one. |

   ControlHome checks the token with `GET devices` before saving. A rejected token, terms not accepted in the LG ThinQ
   app, a country LG does not serve for the account, and the hourly call limit come back as errors on the field to fix.
3. ControlHome creates **one persistent client id** for the integration (`controlhome-<random guid>`, kept in the
   integration's secrets). It is reused for every call and the MQTT connection; LG warns that creating many clients can
   get calls blocked.

### Replacing the token

LG does not publish how long a token lives (one report says about three months). When LG rejects it, the integration
shows **Needs reauth** and stops calling LG. Create a new token and open the integration's flow again (*Integrations →
LG ThinQ → Replace token*): paste the new token (leave it empty to keep the current one while changing the country or the
advanced settings). The client id and the MQTT certificate are kept, so **appliances, rooms, floor-plan positions and
automations stay as they are**.

## What works

Appliances come from `GET devices`; each one's profile (`GET devices/{id}/profile`) becomes capabilities. Everything a
curated mapping does not cover is still there as a **generic** attribute (enums, ranges, booleans and numbers with LG's
read/write access; writable ones get a `set<Name>` command, checked against the profile's values and ranges).

| Appliance | Capabilities |
| --- | --- |
| Air conditioner | `switch` (power), `thermostat` (mode `off`/`cool`/`heat`/`auto`/`dry`/`fan` as the model offers, setpoint with the model's range per mode, current temperature, fan speed), `humidity`, `airQuality` (PM2.5) |
| Washer, dryer, WashTower, WashCombo, styler | `applianceCycle` per unit: state (`idle`/`running`/`paused`/`finished`/`error`), phase (LG's run state), remaining minutes, **remote start** (`remoteControlEnabled`); `start` → `START`, `pause` → `PAUSE` (or `STOP` on models without a pause), `stop` → `POWER_OFF` |
| Dishwasher | `applianceCycle` (as above), door `contact` |
| Refrigerator, kimchi refrigerator, wine cellar | one endpoint per compartment (`fridge`, `freezer`, …) with its target temperature (°C, writable within the model's range); door `contact`; express/rapid freeze, power save, filter as generic attributes |
| Air purifier | `switch`, `airQuality` (PM2.5), `humidity` when reported |
| Dehumidifier / humidifier | `switch`, `humidity`; target humidity and job mode as generic attributes |
| Robot cleaner | `applianceCycle` (`start`, `pause`, `stop` = return to the dock), `battery` |
| Anything else (ovens, hoods, water heaters, …) | a `switch` when the profile has a readable power mode, everything else generic |

- **Parent/child.** A WashTower reported as one appliance (`DEVICE_WASHTOWER`) gets two endpoints, `washer` and `dryer`.
  A WashTower reported as two appliances (`DEVICE_WASHTOWER_WASHER` / `_DRYER`, linked by `groupId`) and a WashCombo
  (`MAIN` / `MINI`) become two devices, the dryer (mini) a child of the washer (main).
- **Notifications** (`DEVICE_PUSH`, e.g. `WASHING_IS_COMPLETE`) become events of the generic momentary `notification`
  attribute (lower-case codes), ready for automations.
- **Commands** are sent as LG's SDK sends them (`POST devices/{id}/control` with `x-conditional-control: true`,
  `{resource: {property: value}}`); several settings of one appliance in one command batch go out in **one** control call.
  Commands are at-most-once: never retried, a repeated idempotency key within 10 min is a duplicate. They are *accepted*
  when LG takes them and confirmed when the appliance reports the new state. Washers, dryers and dishwashers only accept
  `start`/`pause`/`stop` while **remote start** is on at the appliance (ControlHome refuses them before calling LG while
  `remoteControlEnabled` is off).

## Real-time updates and polling

- On every start ControlHome registers the MQTT client (`POST client` with `allowExist`), reuses its stored **client
  certificate** — or creates an RSA-2048 key and a CSR (`CN=lg_thinq`, SHA-512) and asks LG to sign it
  (`POST client/certificate`), at most once a minute — and asks for the broker (`GET route`). The key, certificate and
  topic are saved together in the integration's secrets. (LG's SDK issues a new certificate on every start; ControlHome
  only re-issues when there is none, when it expires within a week, or when the broker refuses it twice in a row.)
- It connects with MQTT 3.1.1 over TLS on port 8883 (client id = the integration's, `CleanSession=false`, QoS 1),
  trusting only the bundled **Amazon Root CA 1** (it is never downloaded at runtime), and subscribes to the topic LG named.
- It subscribes every appliance's push notifications and state events (`POST push/{id}/subscribe`,
  `POST event/{id}/subscribe`; "already subscribed" counts as success) and device-list changes (`POST push/devices`);
  event subscriptions are posted again every 24 hours and after a reconnect (not more than every 10 minutes).
- `DEVICE_STATUS` messages are partial: they are merged into the appliance's last known state (newest message wins).
  Added, removed or renamed appliances trigger a new discovery after 30 s.
- AWS drops MQTT sessions routinely, so **reconnecting is the normal path**: exponential back-off 1 s → 5 min with
  jitter, then the topic is subscribed again. While MQTT is down the integration is *Degraded* and every appliance is
  **polled at most every 5 minutes** (`GET devices/{id}/state`); the same 5-minute rule applies at start-up and after a
  reconnect, so restarts never re-read everything at once.

### Call limits

LG publishes no number for the PAT's hourly limit. Every call (REST, subscriptions, energy, the setup check) shares one
**token bucket per token**: 5 calls at once, then one every 10 s. When LG answers **1306** (call limit exceeded) the
integration shows **Quota exceeded** and makes no call at all for an hour (commands are refused with a clear message
meanwhile); the log line says how many calls were made in the hour before, to tune the budget.

## Energy

LG gives **energy history, not live power**: Wh per day or per month, complete up to yesterday. Once a night, at a random
time between **01:00 and 05:59** local (chosen per integration, like LG's own Home Assistant integration does, to spread
the load), ControlHome asks for every day since the first of last month (`DAILY`) and for last and this month
(`MONTHLY`) — two calls per appliance — and stores them as **energy windows**:

- each day is spread evenly over its 24 hours (`EnergyHourly`, source `vendorWindow`, flagged *estimated* because it is
  not hourly data);
- a month only fills the days its daily data leaves out (its total minus those days);
- the same windows are reported again every night, which is harmless (the rows are replaced, never added).

An appliance whose energy already comes from another source (an energy counter, or a source you chose) is skipped, so
energy is never counted twice. For live watts use a smart plug or a whole-home meter through another connector.

## Limitations

- **Cloud only**: appliances need internet, and so does the server. LG can change or throttle the API at any time.
- Legacy models report state only about every 5 minutes; robot cleaners and microwaves send no notifications.
- Energy is daily (the optional hourly "energy today" of LG's Home Assistant integration is not implemented).
- The time zone of the energy days comes from the advanced setting or the country (Brazil: `America/Sao_Paulo`); accounts
  in other Brazilian zones should set it.
- A WashTower reported as one appliance puts its energy on the `washer` endpoint (LG reports one figure).
- Devices that report only °F setpoints show their temperatures converted to °C where ControlHome maps them; generic
  °F attributes are left out.
- Removing the integration does **not** unregister the MQTT client at LG yet (`DELETE client`): the connector SDK has no
  removal hook. Revoke the token at <https://connect-pat.lgthinq.com> when you remove the integration.
- The energy usage answer's date field (`usedDate`) is taken from LG's other APIs; see the checklist below.

## Troubleshooting

| Status / message | Meaning | What to do |
| --- | --- | --- |
| *Needs reauth* — "rejected the personal access token" (1103, 1218) | The token expired or was revoked. | Create a new token and replace it (see above). |
| *Error* — "has not accepted LG ThinQ's terms" (1304) | The LG account must accept new terms. | Open the LG ThinQ app and accept them; ControlHome retries hourly. |
| *Error* — "does not serve this country" (1307) or "does not know this account" (1202, 1303) | Wrong country. | Replace the token and pick the country the LG account was created in. |
| *Quota exceeded* (1306) | The hourly call limit is used up. | Nothing: calls resume after an hour. Many appliances on one token use more calls at start-up. |
| *Degraded* — "Cannot connect to LG ThinQ's real-time service" | MQTT is blocked or failing. | Allow outbound TCP 8883 from the server. States are polled every 5 minutes meanwhile. |
| A command fails with "Remote control is off" (2301) | Remote start is off at the appliance. | Turn on *Remote Start* on the appliance itself. |
| "cannot do this in its current state" (2302), "powered off" (2304), "current mode" (2305) | The appliance refused in its current state. | Change it on the appliance, or turn it on first. |
| "needs other settings, or several settings in one command" (2207) | The model needs dependent settings together. | Send them in one command batch (automations/scenes). |
| "not connected to LG ThinQ" (1222) | The appliance is offline in LG's cloud. | Check its Wi-Fi in the LG ThinQ app. |

## Live validation checklist

Run once with your own account (Brazil) and note the results in `docs/connectors/LIVE-VALIDATION.md`:

1. Setup with a fresh token and country `BR`: the token check passes and every appliance of the LG ThinQ app appears with
   sensible capabilities (AC thermostat modes and setpoint range, washer/dryer cycle, fridge compartments).
2. The integration reaches **Ok** within a minute: the MQTT client certificate is issued, the broker accepts it (TLS on
   8883), and a state change made in the LG ThinQ app (e.g. AC setpoint) shows up in ControlHome within seconds.
3. Restart ControlHome: no new certificate is requested (check the log line "MQTT client certificate issued") and the
   broker still accepts the stored one. If it does not, note it: LG may require a certificate per start, like its SDK.
4. Commands: AC power, mode, setpoint and fan speed; a washer `start` with remote start on (and the "Remote control is
   off" message with it off); a fridge compartment setpoint.
5. Notifications: a finished wash produces a `notification` event (`washing_is_complete`).
6. Energy: the morning after setup, the energy history shows yesterday's Wh for an appliance with energy data. Check the
   log for "energy windows reported"; if LG's daily answer has no `usedDate` field, note the field name it uses.
7. Wrong token and wrong country errors come back on the right setup fields; a revoked token sets **Needs reauth**, and
   replacing it keeps the appliances in their rooms.
8. Leave it running for a few days: reconnects happen (AWS), the integration returns to **Ok** on its own, and no
   *Quota exceeded* appears with your number of appliances.
