# Phase 1 — Home model, devices core, floor plans Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A simulated home is fully operable: floors and rooms are drawn in an SVG editor, devices from a Simulated connector
are imported through the generic connector SDK, placed on the plan, controlled from tiles/plan/detail with live updates over
SignalR, history is recorded and charted, and a real MQTT connector proves the SDK against a live transport.

**Architecture:** Capability-based device model (`Device → Endpoint → EndpointCapability → Attribute(int id)`), one connector SDK
(`ControlHome.Connectors.Abstractions`) with descriptors, flows and runtimes, a write-behind `StateProcessor` feeding an
in-memory cache + SignalR, `DeviceCommandService` as the only command path (authz, safety policy, audit). Frontend renders every
control through one capability-control registry and one schema-driven form.

**Tech Stack:** as Phase 0 + `Microsoft.AspNetCore.SignalR` (shared framework), `@microsoft/signalr` 10.0.11, SkiaSharp 3.x
(+ `SkiaSharp.NativeAssets.Linux.NoDependencies`) for image re-encoding, ECharts 6.1.0 + vue-echarts 8.3.2, MQTTnet 5.2.0.1603,
Testcontainers (Mosquitto `eclipse-mosquitto:2.1.2`).

**Spec:** approved plan (`C:\Users\samur\.claude\plans\i-want-you-to-foamy-flamingo.md`) · `docs/architecture.md` · Phase 0 conventions.

## Global Constraints
- All Phase 0 constraints (CLAUDE.md) apply. Admin-only for configuration (floors, rooms, integrations, placements);
  Member+ for commands and favorites; Viewer read-only.
- Geometry unit is **centimetres**; floor viewBox origin top-left; polygons are clockwise-agnostic lists of `{x, y}` points.
- Attribute values travel as `StateValue` = `{ kind: 'bool'|'number'|'string'|'json'|'null', b?, n?, s?, j? }` in DTOs and hub messages.
- Every attribute has an int `AttributeId`; all state/history/energy tables key on it.
- Every command goes through `DeviceCommandService`; connectors never receive a command from anywhere else.
- Safety-critical commands (`lock.unlock`, `doorOpener.open`, `cover.open` on gate/garage device class, `alarmPanel.disarm`)
  follow the safety policy (step-up PIN, at-most-once, no retries, audit).
- Room colors come from tokens `--room-1 … --room-8` (light + dark) — added to `tokens.css` in 1.1.
- The Simulated connector is deterministic given a seed (`Simulated:Seed`, default 42) and a `TimeProvider`.

## Review Focus
1. Concurrent edits: two admins saving the same floor layout → second save gets 409 `floors.concurrency` (rowversion) — Task 1.1-A.
2. Self-intersecting / degenerate polygons, rooms outside floor bounds, 0-area rooms → rejected server-side with field errors — 1.1-A.
3. SignalR reconnect after server restart: client resyncs full state, no stale "pending" commands remain — 1.2-D, E2E `realtime-two-clients`.
4. Burst of state updates (1 000/s from a connector) must not stall commands or the UI (bounded channel coalescing) — 1.2-C load test.
5. Device disappears upstream → becomes Orphaned, keeps name/room/placement/history, shown with a badge, never auto-deleted — 1.3-B.

---

## Milestone 1.1 — Floors, rooms and the floor-plan editor

### Task 1.1-A: Floors/rooms/files backend
**Files:** Domain `Homes/Floor.cs`, `Homes/Room.cs`, `Files/StoredFile.cs`, `Geometry/PointCm.cs`, `Geometry/PolygonMath.cs`;
Application `Floors/FloorContracts.cs`, `Floors/FloorService.cs`, `Floors/FloorValidators.cs`, `Files/FileContracts.cs`,
`Files/FileService.cs`, `Files/IImageProcessor.cs`; Infrastructure `Files/SkiaImageProcessor.cs`, configurations; Api
`Features/Floors/FloorEndpoints.cs`, `Features/Files/FileEndpoints.cs`. Tests: `tests/ControlHome.Domain.Tests/Geometry/*`,
`tests/ControlHome.Api.IntegrationTests/Floors/*`, `Files/*`.

**Interfaces (produces):**
- `PolygonMath`: `double AreaCm2(IReadOnlyList<PointCm>)`, `bool IsSimple(IReadOnlyList<PointCm>)` (no self-intersection,
  ≥ 3 distinct points), `bool Contains(IReadOnlyList<PointCm>, PointCm)` (ray casting, boundary counts as inside),
  `PointCm Centroid(IReadOnlyList<PointCm>)`, `bool Intersects(IReadOnlyList<PointCm> a, IReadOnlyList<PointCm> b)`.
- `Floor`: `Id int`, `Name`, `Level int`, `WidthCm int` (default 2000), `HeightCm int` (default 1500), `GridCm int` (default 50),
  `Background` (complex, nullable: `FileId Guid`, `Opacity double` 0–1, `OffsetXCm double`, `OffsetYCm double`,
  `CmPerPx double`), `RowVersion byte[]`, timestamps. `Room`: `Id int`, `FloorId`, `Name`, `Icon string` (lucide/mdi key),
  `ColorToken string` (`room-1`…`room-8`), `Polygon List<PointCm>` (EF complex collection `.ToJson()`), `SortOrder int`.
- `StoredFile`: `Id Guid`, `Purpose string`, `ContentType string`, `Length long`, `Sha256 string`, `WidthPx`, `HeightPx`,
  `Data byte[]` (varbinary(max)), `CreatedAtUtc`.
- DTOs: `FloorSummaryDto(int Id, string Name, int Level, int RoomCount)`, `FloorDto(int Id, string Name, int Level, int WidthCm,
  int HeightCm, int GridCm, FloorBackgroundDto? Background, IReadOnlyList<RoomDto> Rooms, string RowVersion)`,
  `RoomDto(int Id, string Name, string Icon, string ColorToken, IReadOnlyList<PointCmDto> Polygon, double AreaM2, int SortOrder)`,
  `CreateFloorRequest(string Name, int Level, int? WidthCm, int? HeightCm)`, `UpdateFloorRequest(string Name, int Level,
  int WidthCm, int HeightCm, int GridCm, FloorBackgroundDto? Background, string RowVersion)`,
  `SaveFloorLayoutRequest(IReadOnlyList<RoomInput> Rooms, string RowVersion)`, `RoomInput(int? Id, string Name, string Icon,
  string ColorToken, IReadOnlyList<PointCmDto> Polygon)`, `StoredFileDto(Guid Id, string ContentType, long Length, int WidthPx,
  int HeightPx)`, `RoomListItemDto(int Id, string Name, int FloorId, string FloorName)`.
- Endpoints: `GET /api/floors` (Viewer), `GET /api/floors/{id}` (Viewer), `POST /api/floors` (Admin) → 201, `PUT /api/floors/{id}`
  (Admin), `DELETE /api/floors/{id}` (Admin; 409 `floors.not_empty` if devices are placed — from 1.3), `PUT /api/floors/{id}/layout`
  (Admin; replaces rooms atomically; rooms not listed are deleted; 409 `floors.concurrency` on stale rowversion; 400 with
  `errors["rooms[2].polygon"]` codes `geometry.self_intersecting|geometry.too_few_points|geometry.out_of_bounds|geometry.zero_area`),
  `GET /api/rooms` (Viewer), `POST /api/files` (Admin, multipart `file`, `purpose=floor-background`) → 201 `StoredFileDto` |
  400 `files.unsupported_type|files.too_large|files.invalid_image`, `GET /api/files/{id}` (Viewer) → bytes, `Content-Type` stored,
  `X-Content-Type-Options: nosniff`, `Cache-Control: private, max-age=31536000, immutable`.
- Image rules: accept only PNG/JPEG/WebP by magic bytes; ≤ 10 MB; decode with SkiaSharp (fails → `files.invalid_image`);
  downscale so max(width, height) ≤ 4096 px; re-encode as WebP quality 85 (metadata dropped by re-encode); SVG/HTML rejected.

- [ ] Tests first: geometry (square area, bow-tie not simple, point on edge inside, concave polygon containment, centroid of
  L-shape); floors CRUD + layout save + stale rowversion 409 + invalid polygon 400 with path; Member → 403 on writes; file upload
  PNG ok (re-encoded webp, dims), SVG rejected, HTML-renamed-png rejected, 11 MB rejected; GET file headers.
- [ ] Implement; integrator adds migration `FloorsRoomsFiles`; verify `dotnet build -warnaserror && dotnet test`.

### Task 1.1-B: Floor-plan editor UI
**Files:** tokens `--room-1..8` (+ foreground) in `web/packages/ui/src/styles/tokens.css` and theme mapping; shared
`web/apps/web/src/components/floorplan/{FloorPlanCanvas.vue, GridLayer.vue, BackgroundLayer.vue, RoomShape.vue, usePanZoom.ts,
geometry.ts}`; feature `web/apps/web/src/features/floors/{routes.ts, nav.ts, locales/*, pages/FloorsPage.vue,
pages/FloorViewPage.vue, pages/FloorEditorPage.vue, editor/{useEditorState.ts, useCommandStack.ts, useSnapping.ts,
tools/*.ts, EditorToolbar.vue, RoomPropertiesPanel.vue, CalibrationDialog.vue, BackgroundPanel.vue}}`.
- `FloorPlanCanvas` props: `floor: FloorDto`, `mode: 'view'|'edit'`, `selectedRoomId?`; slots `overlay` (devices later);
  emits `room-click`. SVG `viewBox` = floor size in cm; pan (drag background / middle mouse / one-finger), zoom (wheel, pinch,
  buttons), fit-to-screen; room fill `var(--room-N)` with opacity, label at centroid with area m².
- Editor tools: Select (drag room, drag vertex, double-click edge inserts vertex, Delete removes vertex/room), Draw room
  (click adds vertex, click first vertex or Enter closes, Esc cancels), Calibrate (two points on the background + real distance
  → `CmPerPx`), Background (upload, opacity slider, offset by dragging). Snapping (`useSnapping(threshold = 12 screen px)`): to
  grid, to own vertices, to other rooms' vertices and edges (closest wins; Alt disables). Undo/redo (`useCommandStack`, Ctrl+Z,
  Ctrl+Y / Ctrl+Shift+Z) over an editable draft; Save (PUT layout) / Cancel with unsaved-changes guard; client-side validation
  mirrors server codes and shows inline errors; 409 shows "reload" dialog.
- `geometry.ts` mirrors `PolygonMath` (same tests, ported).
- Vitest: geometry, command stack, snapping math, tool state machines (pure functions).
- E2E `floorplan-edit.spec.ts`: create floor → draw 4-vertex room by clicks → rename + color → undo/redo → save → reload
  persists; upload `fixtures/plan.png` → calibrate (two points, 500 cm) → save; Member sees view mode only.

---

## Milestone 1.2 — Devices core

### Task 1.2-A: Capability catalogue + device model (Domain/Application/Infrastructure)
**Files:** Domain `Capabilities/{ValueSchema.cs, AttributeDefinition.cs, CommandDefinition.cs, CapabilityDefinition.cs,
CapabilityCatalog.cs, StateValue.cs}`, `Devices/{Integration.cs, Device.cs, Endpoint.cs, EndpointCapability.cs,
DeviceAttribute.cs, AttributeState.cs, Availability.cs, IntegrationStatus.cs}`; Application `Devices/*Contracts.cs`,
`Devices/DeviceQueryService.cs`, `Capabilities/CapabilityContracts.cs`.
- `ValueSchema` polymorphic records (STJ discriminator `type`): `BoolSchema`, `NumberSchema(double? Min, double? Max, double? Step,
  string? Unit, bool Integer)`, `EnumSchema(IReadOnlyList<string> Values)`, `StringSchema(int? MaxLength)`,
  `FlagsSchema(IReadOnlyList<string> Flags)`, `JsonValueSchema`, `MomentarySchema`.
- `AttributeDefinition(string Key, ValueSchema Schema, string? Unit, StateClass StateClass, AttributeCategory Category,
  bool Recorded, bool Writable)`; `CommandDefinition(string Name, IReadOnlyList<ArgumentDefinition> Arguments,
  ConfirmationMode Confirmation, TimeSpan? Timeout, bool SafetyCritical, bool FailSafe)`; `CapabilityDefinition(string Id,
  int Version, IReadOnlyList<AttributeDefinition> Attributes, IReadOnlyList<CommandDefinition> Commands)`.
- `CapabilityCatalog.All` with ids/attributes/commands exactly as `docs/architecture.md` §capability catalogue lists
  (switch[on; turnOn, turnOff, toggle], level[level %; setLevel(level, transitionMs?)], color[hue, saturation, colorTemperature K;
  setColor, setColorTemperature], thermostat[mode enum off|cool|heat|auto|dry|fan, targetTemperature °C, currentTemperature °C,
  fanMode enum; setMode, setTargetTemperature, setFanMode], power[power W measurement recorded], energy[energy Wh total_increasing
  recorded], temperature, humidity, contact[open], motion[motion], occupancy, lock[lockState enum locked|unlocked|jammed|unknown;
  lock (failSafe), unlock (safetyCritical)], doorOpener[open (safetyCritical)], cover[position %, coverState enum
  open|closed|opening|closing|stopped|unknown; open (safetyCritical when device class gate|garage), close (failSafe), stop,
  setPosition], alarmPanel[armState enum disarmed|armed_home|armed_away|armed_night|triggered|arming; arm(mode), disarm
  (safetyCritical)], mediaPlayer[power, volume %, muted, source, app, playbackState; powerOn, powerOff, setVolume, volumeUp,
  volumeDown, setMuted, selectSource, launchApp, play, pause, stop, next, previous], button[momentary events], battery[battery %
  diagnostic], presence[present], smoke[smoke], waterLeak[leak], airQuality[pm25, co2, voc], camera[streamAvailable; snapshot],
  applianceCycle[machineState enum idle|running|paused|finished|error, phase, remainingMinutes, remoteControlEnabled; start,
  pause, stop gated by remoteControlEnabled], generic[connector-supplied definition]).
- Entities: `Integration(Id Guid, ConnectorKey, Name, ConfigJson json, SecretsProtected string?, Status, StatusMessage, Enabled,
  CreatedAtUtc, UpdatedAtUtc)`, `Device(Id Guid, IntegrationId, ExternalId, ParentDeviceId?, Name, DeviceClass?, Manufacturer?,
  Model?, Firmware?, ConnectionsJson, Availability, LastSeenAtUtc?, IsOrphaned, RoomId?, Icon?, CreatedAtUtc)` unique
  (IntegrationId, ExternalId); `Endpoint(Id Guid, DeviceId, Key, Name?, RoomId?, Icon?, Hidden, Favorite, SortOrder)` unique
  (DeviceId, Key); `EndpointCapability(Id int, EndpointId, CapabilityId, CapabilityVersion, ConstraintsJson?, GenericDefinitionJson?,
  Retired)`; `DeviceAttribute(Id int identity = AttributeId, EndpointCapabilityId, Key)` unique (EndpointCapabilityId, Key);
  `AttributeState(AttributeId PK, Num float?, Str nvarchar(255)?, Json json?, Kind tinyint, ObservedAtUtc, LastChangedAtUtc,
  UpdatedAtUtc)`.
- Tests: catalogue invariants (unique ids/keys, safety flags present on the four commands, every enum non-empty), JSON round
  trip of `ValueSchema` and `StateValue`, argument validation helper `CommandValidator.Validate(CommandDefinition, JsonElement args,
  JsonElement? constraints)` (ranges, enums subset, required).

### Task 1.2-B: Connector SDK + Simulated connector (5 device types)
**Files:** `src/ControlHome.Connectors.Abstractions/*` (new project → Domain; registered in slnx; architecture test rule: references
only Domain), `src/ControlHome.Connectors.Simulated/*` (→ Abstractions only), tests `tests/ControlHome.Connectors.Tests/Simulated/*`.
- Contracts (exact names): `IConnectorDescriptor { string Key; LocalizedText DisplayName; string Icon; ConnectorCategory Category;
  IReadOnlyList<FieldSchema> ConfigFields; IReadOnlyList<FieldSchema> DeviceConfigFields; ConnectorFeatures Features; }`,
  `LocalizedText(string En, string PtBr)`, `FieldSchema(string Key, LocalizedText Label, ValueSchema Schema, bool Required,
  bool Secret, LocalizedText? Help)`, `IConnectorFactory { IConnectorDescriptor Descriptor; IConnectorFlow? CreateSetupFlow();
  IConnectorRuntime CreateRuntime(IntegrationInstance instance); }`, `IntegrationInstance(Guid Id, string Name, JsonElement Config,
  ISecretStore Secrets)`, `IConnectorRuntime : IAsyncDisposable { Task RunAsync(IConnectorContext ctx, CancellationToken ct);
  Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken ct); Task RefreshAsync(string externalDeviceId,
  CancellationToken ct); }`, `IConnectorContext { Guid IntegrationId; TimeProvider Time; ILogger Logger; void ReportState(StateReport r);
  void ReportEvent(EventReport e); void ReportAvailability(string externalDeviceId, Availability a, string? reason);
  Task UpsertDeviceAsync(DiscoveredDevice d, CancellationToken ct); Task RemoveDeviceAsync(string externalDeviceId, CancellationToken ct);
  void SetStatus(IntegrationStatus s, string? message); }`, `StateReport(string ExternalDeviceId, string EndpointKey,
  string CapabilityId, string AttributeKey, StateValue Value, DateTimeOffset? ObservedAtUtc)`, `DiscoveredDevice(string ExternalId,
  string Name, string? DeviceClass, string? Manufacturer, string? Model, string? Firmware, string? ParentExternalId,
  IReadOnlyDictionary<string,string> Connections, string? SuggestedRoom, IReadOnlyList<DiscoveredEndpoint> Endpoints)`,
  `DiscoveredEndpoint(string Key, string? Name, IReadOnlyList<CapabilityBinding> Capabilities)`, `CapabilityBinding(string
  CapabilityId, JsonElement? Constraints, CapabilityDefinition? GenericDefinition)`, `DeviceCommandBatch(string ExternalDeviceId,
  IReadOnlyList<DeviceCommand> Commands, string IdempotencyKey)`, `DeviceCommand(string EndpointKey, string CapabilityId,
  string Command, JsonElement Arguments)`, `CommandOutcome` = `Accepted | Completed | Rejected(string Code, string Message)`.
  Flow types (`IConnectorFlow`, `FlowStep` union: `FormStep`, `ExternalUrlStep`, `ProgressStep`, `ChooseStep`, `DoneStep`,
  `AbortStep`) are declared now, used in 1.3.
- Helpers: `AdaptivePoller` (interval, hot-poll window after command, jitter), `ConnectorErrorClassifier` (401 → NeedsReauth,
  402/plan → PlanRequired, 429 → backoff with Retry-After), `QuotaCounter` interface (persisted impl in Infrastructure).
- Simulated (`key: "simulated"`): config `{ "preset": "basic" | "demo", "seed": 42 }`; basic = dimmable light (switch+level),
  smart plug (switch+power+energy, power random walk when on), AC (switch+thermostat+temperature drifting to target), door
  contact (contact+battery), smart lock (lock, safety). Commands apply after 150 ms simulated latency, report Completed;
  unlock is rejected unless the batch carries the idempotency key once (at-most-once proof: second identical key → Rejected
  `duplicate`). Deterministic with seed + `TimeProvider`.
- Tests: runtime emits initial inventory + states; commands change state; plug energy integrates power over fake time;
  determinism (same seed → same first 20 reports).

### Task 1.2-C: Runtime pipeline (Infrastructure)
**Files:** `Infrastructure/Devices/{DeviceRegistry.cs, StateCache.cs, StateProcessor.cs, StateWriteBehind.cs,
IntegrationSupervisor.cs, ConnectorContext.cs, DeviceInventoryService.cs, RuntimeLease.cs, SecretStore.cs}`,
`Infrastructure/Realtime/IRealtimePublisher.cs` (Application interface) implemented in Api by `HomeHubPublisher`.
- `Channel<StateChange>` bounded 10 000, `FullMode.DropOldest` per attribute via a coalescing dictionary in front (latest value wins).
- `StateProcessor`: resolve AttributeId (registry cache; unknown attribute → created on the fly through inventory), dedupe
  (same value and older/equal observedAt → skip), update `StateCache`, publish `StateDelta(int AttributeId, StateValue Value,
  DateTimeOffset ObservedAtUtc)` batches (≤ 50 ms window) via `IRealtimePublisher`, notify `IStateChangeSubscriber`s (automation
  later), enqueue write-behind. `StateWriteBehind` flushes every 5 s (MERGE into `AttributeState`); on SQL failure keep up to
  50 000 pending rows then drop oldest and raise `HistoryPaused` status.
- `RuntimeLease`: `sp_getapplock @Resource='ControlHome.Runtime', @LockMode='Exclusive', @LockOwner='Session'` on a dedicated
  open connection (re-acquired on reconnect); if not acquired → `RuntimeMode.Standby` (supervisor + processor not started);
  exposed in `GET /api/system/status` → `SystemStatusDto(string RuntimeMode, bool HistoryPaused, string Version)`.
- `IntegrationSupervisor`: per enabled integration create runtime and run with exponential backoff (1 s → 5 min), set status,
  mark that integration's devices `Unknown` while down; stops/starts on integration changes.
- Secrets: `SecretStore` encrypts JSON with `IDataProtector` purpose `ControlHome.Integrations.v1`; `KnownSecretsRedactor`
  (Serilog enricher + destructuring policy) masks any registered secret value in log events; canary test asserts a
  `CANARY-<guid>` secret never appears in logs, responses or hub payloads.
- Tests: pipeline integration (Simulated → cache → hub publisher fake), dedupe, out-of-order drop, write-behind flush,
  SQL-down buffering, lease standby with two hosts, burst test (1 000 reports/s for 5 s, command latency p95 < 500 ms).

### Task 1.2-D: Commands, safety policy, hub, API
**Files:** Application `Devices/DeviceCommandService.cs`, `Devices/CommandContracts.cs`, `Security/SafetyPolicy.cs`,
`Security/StepUpService.cs`; Api `Features/Devices/DeviceEndpoints.cs`, `Features/Capabilities/CapabilityEndpoints.cs`,
`Features/Integrations/IntegrationEndpoints.cs` (minimal in 1.2), `Features/System/SystemEndpoints.cs`, `Realtime/HomeHub.cs`.
- `POST /api/endpoints/{endpointId}/commands` (Member) body `CommandRequest(string CapabilityId, string Command, JsonElement?
  Arguments, string? IdempotencyKey)` → 202 `CommandAcceptedDto(Guid CommandId)` | 400 `commands.invalid_arguments` |
  403 `safety.step_up_required` | 403 `safety.locked` | 404 | 409 `commands.duplicate`. Status updates via hub
  `CommandStatus(Guid CommandId, string Status /*pending|confirmed|failed|timed_out*/, string? Code)`.
- Safety: `PUT /api/auth/me/pin` (`SetPinRequest(string? CurrentPin, string NewPin)`, 4–8 digits, hashed with
  `IPasswordHasher<AppUser>`, stored on AppUser `SafetyPinHash`), `POST /api/auth/step-up` (`StepUpRequest(string Pin)` →
  204, grants 60 s step-up for this user in memory; 5 failures/15 min → 423 `safety.locked`). Safety-critical commands from
  users require an active grant (consumed per command); Viewer never; System principals need `allowSafetyCritical` (1.x: none);
  fail-safe directions never gated. At-most-once: no retry policy, idempotency key required (server generates if absent and
  remembers 10 min).
- Coalescing: `setLevel`, `setVolume`, `setPosition`, `setTargetTemperature`, `setColor*` coalesced per (endpoint, capability)
  300 ms, last wins (earlier command ids report `superseded`).
- Hub `/hubs/home` (`[Authorize]`, cookie): server→client only — `StateChanged(StateDelta[])`, `AvailabilityChanged(Guid DeviceId,
  string Availability)`, `DevicesChanged()`, `IntegrationStatusChanged(Guid Id, string Status, string? Message)`,
  `CommandStatus(...)`, `SystemStatusChanged(SystemStatusDto)`. Hub DTOs added to OpenAPI components via a document transformer.
- Read API: `GET /api/capabilities`, `GET /api/connectors` (descriptors), `GET /api/devices?roomId&integrationId&capability&q`
  (`DeviceSummaryDto` with endpoints, capabilities, AttributeIds and current values), `GET /api/devices/{id}`
  (`DeviceDetailDto`), `PUT /api/devices/{id}` (Admin: name, icon, roomId), `PUT /api/endpoints/{id}` (Admin: name, icon, roomId,
  hidden; Member: favorite only via `PUT /api/endpoints/{id}/favorite`), `GET /api/state` (all current values for resync),
  `GET /api/system/status`, `GET/POST/DELETE /api/integrations` (1.2: create from connector key + config without flow; Admin).
- Audit: `commands.executed` (actor, endpoint, command, outcome; arguments for non-secret commands), `safety.step_up.*`.
- Tests: authz matrix (Viewer/Member/Admin × normal/safety), step-up flow, lockout, duplicate idempotency key → 409 and the
  connector called once (WireMock-delayed variant in 2.x), coalescing, hub receives deltas (SignalR client in tests).

### Task 1.2-E: Devices UI
**Files:** `web/apps/web/src/components/schema/{SchemaForm.vue, SchemaField.vue}`, `components/devices/{DeviceTile.vue,
DeviceIcon.vue, StateBadge.vue, capabilities/registry.ts, capabilities/*Control.vue}`, `stores/{devices.ts, realtime.ts}`,
`composables/{useCommand.ts, useStateValue.ts}`, `features/devices/{routes.ts, nav.ts, locales/*, pages/DevicesPage.vue,
pages/DeviceDetailPage.vue}`, `features/profile` (safety PIN section), `components/safety/SafetyPinDialog.vue`.
- Registry: `registerCapabilityControl(capabilityId, component, { tileSlot?: 'primary'|'secondary' })`; `resolveControl(capabilityId)`
  falls back to `GenericCapabilityControl` (renders each writable attribute with `SchemaField`, commands as buttons).
- Realtime store: connects `@microsoft/signalr` with automatic reconnect; on (re)connect → `GET /api/state` resync; applies
  deltas to a `Map<number, StateValue>`; exposes connection state (banner when disconnected > 3 s).
- `useCommand(endpointId)`: `send(capabilityId, command, args)` → optimistic pending map; safety-critical → opens
  `SafetyPinDialog` → `POST /api/auth/step-up` → retries the command once; shows toast on failure codes.
- Vitest: registry fallback, SchemaField per ValueSchema type, realtime store applying deltas/resync, useCommand pending/timeout.
- E2E `device-control.spec.ts` (create Simulated basic integration via API helper, toggle light from tile, set level, AC target),
  `realtime-two-clients.spec.ts` (two browser contexts; change in one appears in the other < 2 s), `safety-pin.spec.ts`
  (set PIN in profile; unlock lock → PIN dialog → wrong PIN error → right PIN → unlocked; Viewer has no unlock control).

---

## Milestone 1.3 — Devices on the plan, integrations flow, dashboard, demo, export

### Task 1.3-A: Placement + plan devices
- Entity `Placement(Id, FloorId, EndpointId? , DeviceId?, XCm, YCm)` (exactly one of EndpointId/DeviceId). `PUT /api/placements`
  (`PlaceRequest(Guid? EndpointId, Guid? DeviceId, int FloorId, double XCm, double YCm)`) → server assigns RoomId by
  `PolygonMath.Contains` (null if outside every room) and returns `PlacementDto`; `DELETE /api/placements/{id}`;
  `GET /api/floors/{id}` includes `Placements`. Floor delete → 409 `floors.not_empty` when placements exist.
- UI: `FloorPlanCanvas` overlay renders `PlacedDevice.vue` (icon + live state ring: `--state-on/off/offline`); view mode tap →
  `DeviceQuickPanel` popover using the capability registry; editor "Devices" tab lists unplaced endpoints/devices; drag onto plan;
  drag existing to move (room re-assigned); remove.
- E2E `plan-placement.spec.ts`.

### Task 1.3-B: Integrations flow + merge rules
- Flow runtime: `POST /api/integrations/flows` (`StartFlowRequest(string ConnectorKey)`) → `FlowStepDto`; `POST
  /api/integrations/flows/{flowId}` (`ContinueFlowRequest(JsonElement Input)`) → `FlowStepDto`; flows kept in memory 15 min.
  `DoneStep` creates/updates the Integration (secrets encrypted). Reauth: `POST /api/integrations/{id}/flows/reauth`.
- Inventory merge (`DeviceInventoryService.UpsertAsync`): match (IntegrationId, ExternalId); update vendor fields only; new
  endpoints/capabilities added; missing capabilities → `Retired`; device missing in a full inventory sync → `IsOrphaned` (never
  deleted); `POST /api/devices/{id}/replace` (`ReplaceDeviceRequest(Guid NewDeviceId)`) moves name/room/placements/favorites/
  history references to the new device and deletes the old one; duplicate suspicion (same MAC/serial across integrations) flagged.
- UI: `features/integrations` — catalog (descriptor cards), flow wizard (renders FormStep via SchemaForm, ExternalUrl, Progress,
  Choose), integration detail (status, devices, reauth, disable, remove with confirm), import review (select devices, map
  suggested rooms).
- Simulated additions: color light, washer (applianceCycle + power), TV (mediaPlayer), motion sensor (sleepy, battery), 3-gang
  switch (3 endpoints, per-channel W), NVR parent + 4 camera children (snapshot from bundled images), gate (cover gate + contact,
  safety), generic-only device (`generic` capability), setup flow with a Form step + a Progress "press the button" step, and a
  `simulateReauth` config switch that drives NeedsReauth.
- E2E `integration-import.spec.ts`.

### Task 1.3-C: Dashboard v1, demo seed, export/import
- Dashboard: favorites grid (DeviceTile), rooms overview (per room: active devices, temperature, total W), "power now" (sum of
  `power.power`), offline devices, integration health chips. Empty states point to Floors/Integrations.
- Demo seed: `dotnet ControlHome.Api.dll --seed-demo` and setup option "Load demo home" (`CompleteSetupRequest.LoadDemoHome`):
  2 floors (Térreo/Ground, Superior/Upper), 8 rooms with polygons, Simulated integration preset `demo` (~20 devices), placements.
- Export/import: `GET /api/backup/export?includeHistory=false&passphrase=` (Admin) → zip (`manifest.json` with schemaVersion,
  `home.json`, `floors.json`, `files/<id>.webp`, `integrations.json` (secrets only when passphrase given, AES-GCM with
  PBKDF2-SHA256 600k), `devices.json`, `placements.json`, `users.json` (no hashes)); `POST /api/backup/import` (Admin, only when no
  floors/devices exist, else 409 `backup.not_empty`); scheduled daily export to `{DataPath}/backups` keeping 7.
- E2E `export-import.spec.ts`.

---

## Milestone 1.4 — History, energy and MQTT

### Task 1.4-A: History storage + rollups
- Tables exactly per `docs/architecture.md` §Storage: `StateLog`, `StatShortTerm`, `StatHourly`, `RollupProgress`,
  `EnergyHourly`, `DeviceEvent`; PAGE compression; write-behind appends StateLog rows for `Recorded` attributes; rollup service
  (every minute; 5-min buckets then hourly; time-weighted mean for measurements; counters with reset detection; idempotent MERGE
  from watermark); EnergyHourly from energy counters, else integrated power (trapezoid, `Estimated=1`); retention purge job
  (`DELETE TOP (5000)` loops below min(retention, watermark)); settings `GET/PUT /api/settings/retention` (Admin).
- `GET /api/attributes/{id}/history?from&to&resolution=auto|raw|5m|1h` → `HistorySeriesDto(int AttributeId, string Resolution,
  IReadOnlyList<HistoryPointDto> Points)`; `GET /api/energy/hourly?from&to&endpointId?`.
- Tests with FakeTimeProvider: time-weighted mean correctness, counter reset, watermark catch-up after downtime, idempotency,
  purge respects watermark.

### Task 1.4-B: History UI
- `useChartTheme()` from `@controlhome/ui` → ECharts theme object from tokens; `components/charts/{HistoryChart.vue,
  EnergyBars.vue}`; device detail "History" tab per recorded attribute (range picker 24 h / 7 d / 30 d), energy bars for plugs.
- E2E `history-chart.spec.ts` (advance `/api/_test/clock`, chart renders points).

### Task 1.4-C: MQTT connector
- `ControlHome.Connectors.Mqtt` (MQTTnet 5.2): config flow (host, port, TLS, username, password secret, client id, discovery
  prefixes `homeassistant`, Z2M base `zigbee2mqtt`, Tasmota `tasmota/discovery`); own reconnect/resubscribe loop with backoff;
  adapters: Zigbee2MQTT (`bridge/devices` retained → DiscoveredDevice from `definition.exposes` + access bits; state from
  `<base>/<friendly_name>`; commands to `<base>/<friendly_name>/set`; availability from `/availability`; permit-join via flow
  Progress step), HA-discovery intake (device-based + single-component configs; `~` expansion, abbreviations; value_template
  subset: `{{ value_json.x }}`, `| float`, `| int`, `| round(n)`; unsupported → attribute marked unsupported), Tasmota native
  discovery. Fixtures from public Z2M/HA docs recorded with provenance (`Fixtures/Mqtt/SOURCE.md`).
- Tests against Testcontainers `eclipse-mosquitto:2.1.2` (password file auth): discovery → devices, state updates, command
  publishes, reconnect resubscribes, retained-empty config deletes device (→ orphaned).
- Release v0.1 after 1.4 merges.

## Milestone 1.5 — Umbrel template (non-blocking)
- `deploy/umbrel/` template (`umbrel-app.yml`, `docker-compose.yml` with app_proxy, `network_mode: host`, `user: 1000:1000`,
  `${APP_DATA_DIR}/data:/data`, `Setup__Code=${APP_PASSWORD}`, `deterministicPassword: true`, port 8711), `docs/umbrel.md` with
  store-repo creation steps; release.yml step to open a digest-bump PR on the store repo when it exists (skipped otherwise).
