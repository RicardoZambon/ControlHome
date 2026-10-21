# Intelbras cameras, recorders and access control (Dahua HTTP)

> **Experimental: not live-validated.** The connector is tested against a fake device (WireMock.Net) that answers with
> real HTTP Digest authentication and synthetic answers shaped after the publicly hosted *Intelbras — API of HTTP
> Protocol Specification V3.35* (`tests/ControlHome.Connectors.Tests/Fixtures/DahuaHttp/SOURCE.md`), not yet against
> the owner's devices. The **XPE 3200 door-station relay is untested on hardware** and stays behind a feature flag.

Connector key `dahuahttp` (`src/ControlHome.Connectors.DahuaHttp`). Intelbras cameras (VIP, Mibo iM), recorders (NVD,
MHDX, iMHDX) and SS access-control terminals run Dahua-family firmware with a local HTTP "CGI" API. ControlHome talks
to it directly on the LAN — no cloud, no Intelbras app.

**One integration is one physical device.** Add a camera, a recorder or an access terminal each as its own integration
(*Integrations → Add → Intelbras cameras, recorders and access control*).

| Device | Becomes |
| --- | --- |
| IP camera | one device (`camera`): endpoint `main` with `camera` (snapshot, live view) and `motion` |
| Recorder (NVR/DVR) | the recorder (`nvr`, diagnostic channel count) plus **one camera child device per channel** (`<serial>:ch<n>`), each like a camera |
| SS access terminal | one device (`door`): endpoint `main` with `doorOpener` (remote release, safety-critical), `contact` (the door sensor) and a momentary `access` event (`granted` / `denied` / `unknown`) |
| XPE 3200 door station (flag) | one device (`intercom`) with `doorOpener` only |
| Smart rules (IVS) | endpoints `tripwire` (line crossing) and `intrusion` (area) with `motion`, on the camera that has them enabled |
| Alarm inputs | endpoints `alarm1…n` with `contact` (open = input active) on the camera, recorder or terminal |

The device is identified by its **serial number** (`magicBox.cgi?action=getSerialNo`), never by its IP address, so a
new address is just a settings change.

## Before you start (on the device)

Do this in the device's own web page (`http://<device IP>`):

1. **Give the device a fixed IP** (DHCP reservation in the router, or a static address on the device).
2. **Create a dedicated user for ControlHome** (recommended over `admin`): a camera/recorder user needs live view and
   playback-free "view" rights; an access terminal user needs the right to open the door remotely.
3. **Authentication mode: Compatibility (Compatível), MD5 allowed.** ControlHome uses HTTP Digest with MD5 (never
   Basic). Devices in "Safe/Seguro" mode, or with only SHA-256 enabled, answer `401` or `500`. Enabling several digest
   algorithms at once can break RTSP on some firmware: prefer MD5 only.
4. **CGI enabled.** On SS access terminals it is a separate switch: *Segurança → Serviços (Security → Services) → CGI*.
5. **IP filter / allow list** (if you use one): add the ControlHome server's address.
6. **Live view in browsers: H.264.** Browsers play H.264 reliably and H.265 rarely. Set at least the **sub stream**
   (extra stream, `subtype=1`) to H.264; ControlHome uses the sub stream for tiles and the main stream full screen.

## Setting it up

Fields of the setup form:

| Field | Value | Notes |
| --- | --- | --- |
| Device address | the device's IP (e.g. `192.0.2.10`) | Host name or IP, no `http://`. |
| HTTP port | `80` | The device's web/CGI port. |
| User name / Password | the dedicated user | The password is a **secret**: stored encrypted, never returned by the API, never logged. |
| Device type | `auto` | Detected from the model (`getDeviceType`) and the channel count: `SS …`/`ASI…` → access terminal; `NVD`, `MHDX`, `NVR`, `DVR`, `XVR` or more than one channel → recorder; else camera. Pick it yourself if detection guesses wrong. |
| *Advanced:* Recorder channels | empty | Only these channels, e.g. `1-4, 6` (a 16-channel NVR with 4 cameras otherwise imports 16 children). |
| *Advanced:* RTSP port | `554` | For live view. |
| *Advanced:* User ID for remote door release | `101` | Sent as `UserID` with the door release. |
| *Advanced:* Language of device names | the home's | Names the new endpoints ("Linha virtual", "Entrada de alarme 1", …). |

The flow **identifies the device** before saving (`magicBox.cgi?action=getDeviceType` and `getSoftwareVersion`, plus
serial, name and vendor when the firmware answers them). When the device refuses, a **checklist** explains the likely
cause for that answer and offers *Try again* or *Change the settings*:

| Answer | Checklist |
| --- | --- |
| `401` | user and password (log in on the device's web page with them); authentication mode Compatibility + MD5; the **lockout** after failed logins |
| `500` | authentication mode (Compatibility, MD5) — the usual cause of a 500 on the CGI API |
| `404` | the CGI service is off (SS terminals: *Security → Services → CGI*) or the HTTP port is wrong |
| `403` | the user lacks permissions, or an IP filter blocks the server |
| no answer | an error on *Device address* (address, port, device on) |

**Lockout:** Intelbras/Dahua devices lock an address out after a few failed logins (3–5 depending on the model and its
login-failure setting, for 5 to 30 minutes). The flow never retries on its own: *Try again* is accepted at most every
30 s, and after 3 refused logins the flow stops and asks you to wait 30 minutes. A running integration that gets a
`401` waits **30 minutes** before trying again (status *Needs reauthorization*): run the integration's flow again with the
right password instead of waiting.

To change the password or the address later, open the integration's flow again: a blank password keeps the stored one
only while address, port and user stay the same (it is never sent to another device).

## How it works

- **HTTP Digest** through .NET's `SocketsHttpHandler` with a `CredentialCache` for `Digest` only, no proxy, no
  redirects. Every request is answered with a challenge first, then sent once with the Digest response.
- **Startup:** identification, `configManager.cgi?action=getConfig&name=ChannelTitle` (channel names),
  `name=VideoAnalyseRule` (enabled line-crossing / intrusion rules), `alarm.cgi?action=getInSlots` (alarm inputs). The
  devices are reported as one full inventory (a channel no longer imported is orphaned, never deleted).
- **Events:** exactly **one** long-lived `eventManager.cgi?action=attach&codes=[All]&heartbeat=5` per device (firmware
  truncates or rejects long code lists, so everything is subscribed and filtered locally). The multipart stream is read
  with a tolerant parser (CRLF or LF, with or without part headers and `Content-Length`, wrong lengths, an undeclared
  boundary). Mapped codes:

  | Code | Effect |
  | --- | --- |
  | `VideoMotion` | `motion` of the camera (`index` = 0-based channel) |
  | `CrossLineDetection` | `motion` of its `tripwire` endpoint, true for 10 s per crossing |
  | `CrossRegionDetection` | `motion` of its `intrusion` endpoint (Start/Stop) |
  | `AlarmLocal` | `contact.open` of `alarm<index+1>` |
  | `VideoLoss` | the recorder channel is **offline** until the video returns |
  | `AccessControl` | momentary `access` event: `granted` (Status 1), `denied` (Status 0) — data kept: status, method, type, userId, errorCode, door (no card numbers, names or pictures) |
  | `DoorStatus` | `contact.open` of the door (Open / Close) |

  Every other code (`NewFile`, `VideoBlind`, `TimeChange`, …) is ignored. A smart rule or alarm input that was not
  announced at startup gets its endpoint the first time an event names it.
- **Reconnects:** a stream that ends, breaks or stays silent for 20 s (no heartbeat) is reopened after an exponential
  back-off from 1 s to 5 min with ±20 % jitter (reset after a minute of uptime). After every (re)connect the state is
  **read back** — `eventManager.cgi?action=getEventIndexes&code=VideoMotion|CrossRegionDetection|AlarmLocal|VideoLoss`
  and `accessControl.cgi?action=getDoorStatus` — so nothing missed while it was down stays stale. While the device cannot
  be reached its devices are *offline* and the integration *degraded*.
- **Stills:** `snapshot.cgi?channel=N` behind `GET /api/endpoints/{id}/camera/snapshot` (JPEG/PNG only). A camera's
  channel numbering is probed once (1, else 0, depending on the firmware); recorder channels are always 1-based.
- **Door release** (`doorOpener.open`, safety-critical: PIN step-up in the UI, never from Viewers):
  `accessControl.cgi?action=openDoor&channel=1&UserID=<user id>&Type=Remote`. **At most once:** an idempotency key is
  required and a repeated key never reaches the device; the request is sent **once** — never retried, never hedged —
  on connections that are never reused (so the HTTP stack cannot re-send it after a dropped keep-alive connection).
  A late answer is reported as failed (`timeout`: "the door may or may not have opened") and is not repeated.
- **XPE 3200 relay** (only with `Connectors:DahuaHttp:XpeRelay=true`, env `Connectors__DahuaHttp__XpeRelay=true`):
  `GET /fcgi/do?action=OpenDoor&UserName=…&Password=…&DoorNum=1` once, no Digest (the station's own scheme puts the
  credentials in the URL, so it is never logged). **Untested on real hardware**: the URL comes from a second-hand
  description of the station's manual ("Acionar Relé por HTTP" must be enabled on the station). Setup only checks that
  the address answers HTTP; it cannot test the relay without opening the door.

## Camera live view (go2rtc)

Browsers cannot play RTSP, so live view runs through the **go2rtc** sidecar (compose profile `cameras`,
`alexxit/go2rtc:1.9.14` pinned by digest). The browser only ever talks to ControlHome:

- `GET /api/endpoints/{id}/camera/stream?quality=main|sub` — a WebSocket relayed to go2rtc's `api/ws` (go2rtc's
  video-rtc.js protocol: MSE/fMP4 segments, or WebRTC signalling).
- `POST /api/endpoints/{id}/camera/webrtc` — a receive-only SDP offer in, go2rtc's answer out. Media then flows
  directly between the browser and go2rtc on **TCP/UDP 8555** of the host.

Anyone signed in (Viewer included) may watch. Unknown endpoints and endpoints that are not cameras are `404`. How the
camera's credentials stay on the server:

- ControlHome asks the connector for the stream (`rtsp://<user>:<password>@<host>:554/cam/realmonitor?channel=N&subtype=0|1`)
  and registers it in go2rtc at runtime with `PATCH /api/streams` under an **opaque name** (`ch_<endpoint id>_main|sub`):
  in memory only — go2rtc writes `PUT`s into its config file, `PATCH`es never, and `go2rtc.yaml` is mounted read-only.
- go2rtc's API listens on **127.0.0.1:1984** with random credentials and `local_auth` (credentials even from loopback,
  where every process on the host is "local" with host networking); `allow_paths` registers only `/api/streams`,
  `/api/ws` and `/api/webrtc` (no web UI, no `/api/config`, `/api/exit`, …); the RTSP server is off (`rtsp.listen: ""`);
  only the modules `api, ws, rtsp, webrtc, mp4` load (no `exec`, `ffmpeg`, `echo`, `expr`; `exec.allow_paths: []`
  anyway).
- The relay forwards to go2rtc only the viewer messages `mse`, `mp4`, `mjpeg`, `webrtc/offer` (receive-only SDP) and
  `webrtc/candidate`; any other message is dropped, binary or oversized messages close the socket. WebRTC offers that
  would *send* media (publishing into a camera's stream, two-way audio) or open data channels are refused. A client's
  own `src`/`dst` parameters are ignored. The session is re-checked every minute (a disabled or demoted user's view
  closes), at most 32 views are open at once (`Cameras:Go2Rtc:MaxLiveSessions`) and one user starts at most 30 views
  (WebSocket or WebRTC) a minute (`429 request.rate_limited`).

### Turning it on

1. In `deploy/.env` set `GO2RTC_API_USERNAME` and `GO2RTC_API_PASSWORD` to long random values (`openssl rand -hex 24`
   each; go2rtc refuses to start with a password shorter than 16 characters) and `CONTROLHOME_LAN_IP` to the host's
   LAN address. The app reads the same values (`Cameras__Go2Rtc__Url/Username/Password`); with empty credentials live
   view stays off (`503 cameras.live_disabled`) and stills keep working.
2. `docker compose -f deploy/docker-compose.yml --profile cameras up -d`. go2rtc runs with host networking (like the
   app), read-only, all capabilities dropped, as the app's UID.
3. Open TCP and UDP **8555** to the LAN if the host has a firewall (WebRTC media). MSE over the WebSocket needs nothing
   extra.

The development compose (`docker-compose.dev.yml`) runs go2rtc on the project's bridge network instead: its API listens
on `:1984` inside that network only (not published; the app reaches it as `http://go2rtc:1984`), WebRTC is published on
8555 and announced at `CONTROLHOME_LAN_IP`.

`deploy/go2rtc.yaml` is committed without secrets: go2rtc fills `${VAR}` placeholders from its environment when it reads
the file.

## Limitations

- HTTP only (no HTTPS to the device); keep cameras on a trusted LAN or VLAN.
- One door per access terminal (door 1); multi-door controllers, card/user management and "modo online" are not
  supported. The access terminal's own camera is not offered as a camera.
- No ONVIF fallback for non-Dahua cameras, no PTZ, no playback, no two-way audio.
- No push receivers yet: the SS terminals' *Serviço de eventos* HTTP push, XPE *Ações URL* callbacks and the syslog
  listener are not implemented (the attach stream is the only event source).
- VTO video door stations (DHIP on TCP 5000) are not supported.
- go2rtc has no ffmpeg/transcoding in this setup: H.265-only cameras will not play in most browsers.
- Endpoints added by an event (an IVS rule or alarm input not announced at startup) are retired after a restart until
  their next event, unless the device announces them at startup.
- The device's lockout policy is not read from the device; ControlHome simply never retries a refused login quickly.

## Troubleshooting

- *The device refused the user name or password (401)*: log in on the device's web page with the same user; check
  Compatibility authentication + MD5; if it was locked out, wait (often 30 minutes) — retrying only extends the lock.
- *Internal error (500)* on every call: authentication mode (see above).
- *No such CGI (404)*: enable CGI (SS terminals: *Security → Services*); check the HTTP port.
- A recorder shows 16 cameras but has 4: set *Recorder channels* (Advanced), e.g. `1-4`.
- Motion never arrives: motion detection must be enabled (and armed in the schedule) on the device for each channel;
  the integration status shows *degraded* while the event stream reconnects.
- Live view says *not set up* (`cameras.live_disabled`): `GO2RTC_API_USERNAME`/`GO2RTC_API_PASSWORD` are empty for the
  app. *Not available* (`cameras.live_unavailable`): go2rtc is not running (`docker compose --profile cameras ps`) or the
  credentials differ between the two services.
- Live view plays over MSE but not WebRTC: `CONTROLHOME_LAN_IP` is unset or wrong, or TCP/UDP 8555 is blocked.
- A black picture in the browser: the stream is H.265 — switch the (sub) stream to H.264 on the device.

## Live validation checklist

Run these with the owner's devices before the connector loses its *Experimental* label (results go to
`docs/connectors/LIVE-VALIDATION.md`):

- [ ] Camera (e.g. VIP series): setup with `auto` detects *camera*; serial, model and firmware shown; a wrong password
      shows the 401 checklist and the device does **not** lock out after one try.
- [ ] Authentication mode: the checklist text matches the firmware's menu names (Compatibility / MD5, Security →
      Services → CGI); a 500 in "Safe" mode is explained.
- [ ] Recorder (NVD/MHDX): channels and their names import as children; *Recorder channels* limits them; unplugging a
      camera from the recorder marks its child offline (VideoLoss) and back online.
- [ ] Motion: walking in front of a camera sets `motion` within ~1 s and clears it; an IVS line-crossing rule produces a
      `tripwire` pulse; an intrusion rule sets and clears `intrusion`.
- [ ] Unplug the device's network for a minute: the integration goes *degraded*, devices *offline*; on reconnect the
      state is read back (motion active during the outage is not stuck).
- [ ] Stills: `snapshot` works on the camera and on every recorder channel (record which channel numbering the camera
      uses: 1-based or 0-based).
- [ ] Live view: tile (sub stream) and full screen (main stream) play in Chrome, Firefox and Safari/iOS over MSE and over
      WebRTC; a Viewer can watch; a disabled user's open view closes within a minute.
- [ ] go2rtc: `curl http://127.0.0.1:1984/api/streams` without credentials is `401`; `/api/config` with credentials is
      `404`; port 1984 is not reachable from another machine on the LAN; `go2rtc.yaml` on disk never contains a stream.
- [ ] Access terminal (SS 3530/3532/5530…): door contact follows the real door; a card/face access shows `granted`, an
      unknown card `denied`; the remote release opens the door exactly once (PIN step-up), also when the network is
      slow; record the `UserID` the terminal expects.
- [ ] XPE 3200 (with `Connectors__DahuaHttp__XpeRelay=true`): enable *Acionar Relé por HTTP* on the station; check that
      `DoorNum=1` is the right relay and that the release fires once. Only then remove "untested on hardware".
