# Dahua HTTP connector fixtures — provenance

Every file here is **synthetic**: hand-written for these tests, shaped after the request and answer examples of the
publicly hosted *Intelbras — API of HTTP Protocol Specification V3.35* (2023-07-21; the Dahua-family CGI API Intelbras
publishes for its cameras, recorders and access terminals) and the public *Dahua Access Control Products Integration
Instruction* (openDoor, AccessControl / DoorStatus events). No answer was captured from a real device: models are
public Intelbras model names, but serial numbers (`SYNTH…`), user ids, card numbers (`00000000`, `11111111`), file
paths, names and timestamps are made up, and the tests address the fake device on `127.0.0.1` (WireMock) or use
documentation addresses (`192.0.2.x`).

| File | Shape taken from | Notes |
| --- | --- | --- |
| `event-stream-camera.txt` | `eventManager.cgi?action=attach&codes=[All]&heartbeat=5`: `multipart/x-mixed-replace; boundary=myboundary`, parts with `Content-Type: text/plain`, bodies `Code=<code>;action=Start\|Stop\|Pulse;index=<n>[;data={…}]` and the `Heartbeat` keep-alive part | VideoMotion start/stop, a line-crossing (`CrossLineDetection`) event with a multi-line `data` object, an alarm input (`AlarmLocal`), and an unmapped `NewFile` event on purpose. |
| `event-stream-recorder.txt` | Same stream from a recorder: `index` is the 0-based video channel | Motion on channel 3, video loss on channel 2, an intrusion (`CrossRegionDetection`) on channel 1 that no rule announced, a part carrying **two** events, and an event for a channel the recorder does not have (index 9). |
| `event-stream-access.txt` | The access-terminal stream: `AccessControl` (`Status` 1 granted / 0 denied, `Method`, `UserID`, `CardNo`, `Type`, `ErrorCode`) and `DoorStatus` (`Status` Open / Close) events | Card number, name and picture path are present on purpose: the connector must drop them. |
| `channel-title-recorder.txt` | `configManager.cgi?action=getConfig&name=ChannelTitle` → `table.ChannelTitle[<i>].Name=<title>` | Four channels, the last one without a title. |
| `video-analyse-rule.txt` | `configManager.cgi?action=getConfig&name=VideoAnalyseRule` → `table.VideoAnalyseRule[<channel>][<rule>].Type/Enable/Name/Class` | An enabled line-crossing rule and a disabled intrusion rule on channel 1, an enabled intrusion rule on channel 3. |

The fixtures are stored with LF line ends (repository rule). The tests rebuild the stream as devices send it — CRLF line
ends and a `Content-Length` header per part (`DahuaFixtures.Multipart`) — and also feed the LF-only form, byte by byte,
without lengths and with wrong lengths, to cover firmware differences. Short key/value answers (`type=…`,
`version=…`, `sn=…`, `result=…`, `channels[0]=…`, `Info.status=…`, `OK`, `Error`) are written inline in the tests in the
same format.
