using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Floors;
using ControlHome.Application.Identity;
using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Files;
using ControlHome.Domain.Geometry;
using ControlHome.Domain.Homes;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.HomeBackup;

/// <summary>
/// Export and import of the home configuration as a zip archive (schema <see cref="SchemaVersion"/>): <c>manifest.json</c>,
/// <c>home.json</c>, <c>floors.json</c> (with rooms), <c>files.json</c> + <c>files/&lt;id&gt;.&lt;ext&gt;</c>,
/// <c>integrations.json</c> (secrets only when exported with a passphrase, encrypted with <see cref="BackupCipher"/>),
/// <c>devices.json</c>, <c>endpoints.json</c> (with capabilities and attribute keys), <c>placements.json</c> and
/// <c>users.json</c> (no password hashes, no PINs). History is not part of a backup. An import goes only into an empty home
/// (no floors, integrations or devices), in one transaction, and starts the imported integrations.
/// </summary>
public sealed class BackupService(
    IControlHomeDb db,
    UserManager<AppUser> userManager,
    IIntegrationSecretProtector secretProtector,
    IIntegrationRuntimeControl runtime,
    IRuntimeStatus runtimeStatus,
    IExclusiveTransaction exclusive,
    IRealtimePublisher publisher,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const int SchemaVersion = 1;
    public const string AppName = "ControlHome";
    public const string LockName = "ControlHome.Backup";
    public const string BackupTarget = "backup";
    public const string ContentType = "application/zip";

    /// <summary>Largest archive entry read on import (a floor image is at most 10 MB; JSON documents are far smaller).</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 10_000;

    /// <summary>
    /// The current configuration as a backup archive; with <paramref name="passphrase"/> the integrations' secrets are
    /// included, encrypted. <paramref name="audit"/>: record <c>backup.exported</c> (user exports; not the scheduled one).
    /// </summary>
    public async Task<BackupArchive> ExportAsync(string? passphrase, bool audit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var home = await db.Homes.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var floors = await db.Floors.AsNoTracking().Include(f => f.Rooms).OrderBy(f => f.Level).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        var files = await db.StoredFiles.AsNoTracking().OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        var integrations = await db.Integrations.AsNoTracking().OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id).ToListAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.CreatedAtUtc).ThenBy(d => d.Id).ToListAsync(cancellationToken);
        var endpoints = await db.Endpoints.AsNoTracking()
            .Include(e => e.Capabilities)
            .ThenInclude(c => c.Attributes)
            .OrderBy(e => e.DeviceId)
            .ThenBy(e => e.SortOrder)
            .ToListAsync(cancellationToken);
        var placements = await db.Placements.AsNoTracking().OrderBy(p => p.Id).ToListAsync(cancellationToken);
        var users = new List<UserRecord>();
        foreach (var user in await userManager.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken))
        {
            var role = (await userManager.GetRolesAsync(user)).OrderBy(r => r, StringComparer.Ordinal).FirstOrDefault() ?? Roles.Viewer;
            users.Add(new UserRecord(user.UserName!, user.DisplayName, role, user.Locale, user.Theme, user.IsDisabled));
        }

        var (encryption, key) = passphrase is null ? (null, null) : BackupCipher.Create(passphrase);
        var integrationRecords = integrations.Select(i => new IntegrationRecord(
                i.Id,
                i.ConnectorKey,
                i.Name,
                Parse(i.ConfigJson),
                i.Enabled,
                i.CreatedAtUtc,
                key is null ? null : EncryptSecrets(key, i)))
            .ToList();

        var manifest = new BackupManifest(
            SchemaVersion,
            AppName,
            AppVersion.Informational,
            now,
            IncludesSecrets: key is not null,
            IncludesHistory: false,
            encryption,
            new BackupCounts(floors.Count, floors.Sum(f => f.Rooms.Count), files.Count, integrations.Count, devices.Count, endpoints.Count, placements.Count, users.Count));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, BackupEntries.Manifest, manifest);
            Write(zip, BackupEntries.Home, home is null ? null : new HomeRecord(
                home.Name,
                home.TimeZoneId,
                home.Latitude,
                home.Longitude,
                home.Locale,
                home.Currency,
                home.Tariff.Kind,
                home.Tariff.PricePerKwh));
            Write(zip, BackupEntries.Floors, floors.Select(f => new FloorRecord(
                f.Id,
                f.Name,
                f.Level,
                f.WidthCm,
                f.HeightCm,
                f.GridCm,
                f.Background is null ? null : new BackgroundRecord(f.Background.FileId, f.Background.Opacity, f.Background.OffsetXCm, f.Background.OffsetYCm, f.Background.CmPerPx),
                [.. f.Rooms.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(r => new RoomRecord(r.Id, r.Name, r.Icon, r.ColorToken, r.SortOrder, [.. r.Polygon.Select(p => new PointRecord(p.X, p.Y))]))])));
            var fileRecords = files.Select(f => new FileRecord(f.Id, f.Purpose, f.ContentType, f.Length, f.Sha256, f.WidthPx, f.HeightPx, f.CreatedAtUtc, FilePath(f))).ToList();
            Write(zip, BackupEntries.Files, fileRecords);
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(FilePath(file), CompressionLevel.NoCompression); // already compressed images
                await using var stream = entry.Open();
                await stream.WriteAsync(file.Data, cancellationToken);
            }

            Write(zip, BackupEntries.Integrations, integrationRecords);
            Write(zip, BackupEntries.Devices, devices.Select(d => new DeviceRecord(
                d.Id,
                d.IntegrationId,
                d.ExternalId,
                d.ParentDeviceId,
                d.Name,
                d.DeviceClass,
                d.Manufacturer,
                d.Model,
                d.Firmware,
                Parse(d.ConnectionsJson),
                d.IsOrphaned,
                d.RoomId,
                d.Icon,
                d.CreatedAtUtc)));
            Write(zip, BackupEntries.Endpoints, endpoints.Select(e => new EndpointRecord(
                e.Id,
                e.DeviceId,
                e.Key,
                e.Name,
                e.RoomId,
                e.Icon,
                e.Hidden,
                e.Favorite,
                e.SortOrder,
                [
                    .. e.Capabilities.OrderBy(c => c.CapabilityId, StringComparer.Ordinal).Select(c => new CapabilityRecord(
                        c.CapabilityId,
                        c.CapabilityVersion,
                        c.ConstraintsJson is null ? null : Parse(c.ConstraintsJson),
                        c.GenericDefinitionJson is null ? null : Parse(c.GenericDefinitionJson),
                        c.Retired,
                        [.. c.Attributes.Select(a => a.Key).Order(StringComparer.Ordinal)])),
                ])));
            Write(zip, BackupEntries.Placements, placements.Select(p => new PlacementRecord(p.FloorId, p.EndpointId, p.DeviceId, p.XCm, p.YCm)));
            Write(zip, BackupEntries.Users, users);
        }

        if (audit)
        {
            await auditLog.WriteAsync(
                new AuditEvent(AuditActions.BackupExported, BackupTarget, null, new { manifest.IncludesSecrets, manifest.Counts }),
                cancellationToken);
        }

        return new BackupArchive(
            string.Create(CultureInfo.InvariantCulture, $"controlhome-backup-{now.UtcDateTime:yyyyMMdd-HHmmss}.zip"),
            buffer.ToArray(),
            manifest);
    }

    /// <summary>
    /// Restores a backup into an empty home. Errors: 400 <c>backup.invalid</c> (not a backup, damaged, inconsistent),
    /// 400 <c>backup.unsupported_version</c>, 400 <c>backup.passphrase_required</c> / <c>backup.passphrase_incorrect</c>
    /// (secrets in the backup), 409 <c>backup.not_empty</c> (floors, integrations or devices exist), 409
    /// <c>runtime.standby</c>, 503 <c>runtime.starting</c>.
    /// </summary>
    public async Task<Result<BackupImportResultDto>> ImportAsync(Stream archive, string? passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (runtimeStatus.IntegrationChangeRefusal() is { } refused)
        {
            return refused;
        }

        ParsedBackup backup;
        try
        {
            backup = await ReadAsync(archive, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return Invalid();
        }

        if (backup.Manifest.SchemaVersion > SchemaVersion)
        {
            return AppError.Validation(ErrorCodes.BackupUnsupportedVersion, "This backup comes from a newer version of ControlHome.");
        }

        var secrets = new Dictionary<Guid, IntegrationSecrets>();
        if (backup.Manifest.IncludesSecrets)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                return AppError.Validation(ErrorCodes.BackupPassphraseRequired, "This backup holds encrypted secrets: give its passphrase.");
            }

            byte[]? key;
            try
            {
                key = backup.Manifest.Encryption is { } encryption ? BackupCipher.Open(encryption, passphrase) : throw new FormatException("No encryption parameters.");
            }
            catch (FormatException)
            {
                return Invalid();
            }

            if (key is null)
            {
                return AppError.Validation(ErrorCodes.BackupPassphraseIncorrect, "The passphrase does not open this backup's secrets.");
            }

            try
            {
                foreach (var integration in backup.Integrations.Where(i => i.Secrets is not null))
                {
                    var json = BackupCipher.Decrypt(key, integration.Secrets!, SecretsContext(integration.Id));
                    var record = JsonSerializer.Deserialize<SecretsRecord>(json, BackupEntries.Json) ?? throw new JsonException("No secrets.");
                    secrets[integration.Id] = new IntegrationSecrets(record.Values, record.Devices);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                return Invalid();
            }
        }

        var firstFloor = backup.Floors.Count > 0;
        var result = await exclusive.RunAsync(
            LockName,
            token => ImportInTransactionAsync(backup, secrets, token),
            async token => firstFloor
                ? await db.Floors.AnyAsync(token)
                : backup.Integrations.Count > 0 && await db.Integrations.AnyAsync(token),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        foreach (var integration in backup.Integrations.Where(i => i.Enabled))
        {
            await runtime.StartAsync(integration.Id, cancellationToken);
        }

        await publisher.DevicesChangedAsync(cancellationToken);
        return result.Value with { SecretsRestored = secrets.Count > 0 };
    }

    private async Task<Result<BackupImportResultDto>> ImportInTransactionAsync(
        ParsedBackup backup,
        IReadOnlyDictionary<Guid, IntegrationSecrets> secrets,
        CancellationToken cancellationToken)
    {
        if (await db.Floors.AnyAsync(cancellationToken)
            || await db.Integrations.AnyAsync(cancellationToken)
            || await db.Devices.AnyAsync(cancellationToken))
        {
            return AppError.Conflict(ErrorCodes.BackupNotEmpty, "A backup can only be imported into an empty home.");
        }

        var now = timeProvider.GetUtcNow();
        if (backup.Home is { } homeRecord && await db.Homes.SingleOrDefaultAsync(cancellationToken) is { } home)
        {
            home.Name = homeRecord.Name;
            home.TimeZoneId = homeRecord.TimeZoneId;
            home.Latitude = homeRecord.Latitude;
            home.Longitude = homeRecord.Longitude;
            home.Locale = homeRecord.Locale;
            home.Currency = homeRecord.Currency;
            home.Tariff = new Tariff { Kind = homeRecord.TariffKind, PricePerKwh = homeRecord.PricePerKwh };
        }

        var existingFiles = (await db.StoredFiles.Select(f => f.Id).ToListAsync(cancellationToken)).ToHashSet();
        var filesAdded = 0;
        foreach (var file in backup.Files.Where(f => !existingFiles.Contains(f.Record.Id)))
        {
            db.StoredFiles.Add(new StoredFile
            {
                Id = file.Record.Id,
                Purpose = file.Record.Purpose,
                ContentType = file.Record.ContentType,
                Length = file.Data.Length,
                Sha256 = file.Record.Sha256,
                WidthPx = file.Record.WidthPx,
                HeightPx = file.Record.HeightPx,
                Data = file.Data,
                CreatedAtUtc = file.Record.CreatedAtUtc,
            });
            filesAdded++;
        }

        var floors = new Dictionary<int, Floor>();
        var rooms = new Dictionary<int, Room>();
        foreach (var record in backup.Floors)
        {
            var floor = new Floor
            {
                Name = record.Name,
                Level = record.Level,
                WidthCm = record.WidthCm,
                HeightCm = record.HeightCm,
                GridCm = record.GridCm,
                Background = record.Background is { } background
                    && (backup.Files.Any(f => f.Record.Id == background.FileId) || existingFiles.Contains(background.FileId))
                    ? new FloorBackground
                    {
                        FileId = background.FileId,
                        Opacity = background.Opacity,
                        OffsetXCm = background.OffsetXCm,
                        OffsetYCm = background.OffsetYCm,
                        CmPerPx = background.CmPerPx,
                    }
                    : null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            foreach (var roomRecord in record.Rooms)
            {
                var room = new Room
                {
                    Name = roomRecord.Name,
                    Icon = roomRecord.Icon,
                    ColorToken = roomRecord.ColorToken,
                    SortOrder = roomRecord.SortOrder,
                    Polygon = [.. roomRecord.Polygon.Select(p => new PointCm(p.X, p.Y))],
                };
                floor.Rooms.Add(room);
                rooms[roomRecord.Id] = room;
            }

            floors[record.Id] = floor;
            db.Floors.Add(floor);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var record in backup.Integrations)
        {
            db.Integrations.Add(new Integration
            {
                Id = record.Id,
                ConnectorKey = record.ConnectorKey,
                Name = record.Name,
                ConfigJson = record.Config.GetRawText(),
                SecretsProtected = secrets.TryGetValue(record.Id, out var integrationSecrets) ? secretProtector.Protect(integrationSecrets) : null,
                Status = record.Enabled ? IntegrationStatus.Connecting : IntegrationStatus.Stopped,
                Enabled = record.Enabled,
                CreatedAtUtc = record.CreatedAtUtc,
                UpdatedAtUtc = now,
            });
        }

        var deviceIds = backup.Devices.Select(d => d.Id).ToHashSet();
        var devices = new List<Device>();
        foreach (var record in backup.Devices)
        {
            var device = new Device
            {
                Id = record.Id,
                IntegrationId = record.IntegrationId,
                ExternalId = record.ExternalId,
                Name = record.Name,
                DeviceClass = record.DeviceClass,
                Manufacturer = record.Manufacturer,
                Model = record.Model,
                Firmware = record.Firmware,
                ConnectionsJson = record.Connections.GetRawText(),
                Availability = Availability.Unknown,
                IsOrphaned = record.IsOrphaned,
                RoomId = record.RoomId is { } roomId && rooms.TryGetValue(roomId, out var room) ? room.Id : null,
                Icon = record.Icon,
                CreatedAtUtc = record.CreatedAtUtc,
            };
            devices.Add(device);
            db.Devices.Add(device);
        }

        foreach (var record in backup.Endpoints)
        {
            db.Endpoints.Add(new Endpoint
            {
                Id = record.Id,
                DeviceId = record.DeviceId,
                Key = record.Key,
                Name = record.Name,
                RoomId = record.RoomId is { } roomId && rooms.TryGetValue(roomId, out var room) ? room.Id : null,
                Icon = record.Icon,
                Hidden = record.Hidden,
                Favorite = record.Favorite,
                SortOrder = record.SortOrder,
                Capabilities =
                [
                    .. record.Capabilities.Select(c => new EndpointCapability
                    {
                        CapabilityId = c.CapabilityId,
                        CapabilityVersion = c.CapabilityVersion,
                        ConstraintsJson = c.Constraints is { ValueKind: JsonValueKind.Object } constraints ? constraints.GetRawText() : null,
                        GenericDefinitionJson = c.GenericDefinition is { ValueKind: JsonValueKind.Object } definition ? definition.GetRawText() : null,
                        Retired = c.Retired,
                        Attributes = [.. c.Attributes.Distinct(StringComparer.Ordinal).Select(key => new DeviceAttribute { Key = key })],
                    }),
                ],
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Parent links once every device exists.
        foreach (var device in devices)
        {
            var parent = backup.Devices.Single(d => d.Id == device.Id).ParentDeviceId;
            if (parent is { } parentId && parentId != device.Id && deviceIds.Contains(parentId))
            {
                device.ParentDeviceId = parentId;
            }
        }

        var endpointIds = backup.Endpoints.Select(e => e.Id).ToHashSet();
        var placements = 0;
        foreach (var record in backup.Placements)
        {
            if (!floors.TryGetValue(record.FloorId, out var floor)
                || (record.EndpointId is { } endpointId ? !endpointIds.Contains(endpointId) : record.DeviceId is not { } deviceId || !deviceIds.Contains(deviceId)))
            {
                continue;
            }

            db.Placements.Add(new Placement
            {
                FloorId = floor.Id,
                EndpointId = record.EndpointId,
                DeviceId = record.EndpointId is null ? record.DeviceId : null,
                XCm = record.XCm,
                YCm = record.YCm,
            });
            placements++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var usersCreated = 0;
        foreach (var record in backup.Users)
        {
            if (await userManager.FindByNameAsync(record.UserName) is not null)
            {
                continue;
            }

            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                UserName = record.UserName,
                DisplayName = record.DisplayName,
                Locale = record.Locale,
                Theme = record.Theme,
                IsDisabled = record.IsDisabled,
                CreatedAtUtc = now,
            };

            // No password: an admin sets one with a password reset before the person can sign in.
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded || !(await userManager.AddToRoleAsync(user, Roles.All.Contains(record.Role) ? record.Role : Roles.Viewer)).Succeeded)
            {
                return Invalid();
            }

            usersCreated++;
        }

        var dto = new BackupImportResultDto(
            floors.Count,
            rooms.Count,
            filesAdded,
            backup.Integrations.Count,
            devices.Count,
            placements,
            usersCreated,
            SecretsRestored: false);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.BackupImported,
                BackupTarget,
                null,
                new { dto.Floors, dto.Rooms, dto.Files, dto.Integrations, dto.Devices, dto.Placements, dto.UsersCreated, SecretsRestored = secrets.Count > 0, backup.Manifest.ExportedAtUtc }),
            cancellationToken);
        return dto;
    }

    /// <summary>Reads and checks the whole archive before anything is written.</summary>
    private static async Task<ParsedBackup> ReadAsync(Stream archive, CancellationToken cancellationToken)
    {
        // ZipArchive needs a seekable stream; the request body is buffered first (bounded by the endpoint's size limit).
        var seekable = archive;
        if (!archive.CanSeek)
        {
            seekable = new MemoryStream();
            await archive.CopyToAsync(seekable, cancellationToken);
            seekable.Position = 0;
        }

        using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("Too many entries.");
        }

        var manifest = Read<BackupManifest>(zip, BackupEntries.Manifest);
        if (manifest.App != AppName || manifest.SchemaVersion < 1)
        {
            throw new InvalidDataException("Not a ControlHome backup.");
        }

        if (manifest.SchemaVersion > SchemaVersion)
        {
            return new ParsedBackup(manifest, null, [], [], [], [], [], [], []);
        }

        var home = zip.GetEntry(BackupEntries.Home) is null
            ? null
            : JsonSerializer.Deserialize<HomeRecord?>(ReadBytes(zip, BackupEntries.Home), BackupEntries.Json);
        var floors = Read<List<FloorRecord>>(zip, BackupEntries.Floors);
        var fileRecords = Read<List<FileRecord>>(zip, BackupEntries.Files);
        var files = new List<ParsedFile>();
        foreach (var record in fileRecords)
        {
            var data = ReadBytes(zip, record.Path);
            if (!FilePurposes.All.Contains(record.Purpose) || record.ContentType is not ("image/webp" or "image/png" or "image/jpeg")
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"File {record.Id} does not match its record.");
            }

            files.Add(new ParsedFile(record, data));
        }

        var integrations = Read<List<IntegrationRecord>>(zip, BackupEntries.Integrations);
        var devices = Read<List<DeviceRecord>>(zip, BackupEntries.Devices);
        var endpoints = Read<List<EndpointRecord>>(zip, BackupEntries.Endpoints);
        var placements = Read<List<PlacementRecord>>(zip, BackupEntries.Placements);
        var users = Read<List<UserRecord>>(zip, BackupEntries.Users);

        var parsed = new ParsedBackup(manifest, home, floors, files, integrations, devices, endpoints, placements, users);
        Check(parsed);
        return parsed;
    }

    /// <summary>The checks the database would otherwise answer with an exception (lengths, references, uniqueness, geometry).</summary>
    private static void Check(ParsedBackup backup)
    {
        void Require(bool condition, string what)
        {
            if (!condition)
            {
                throw new InvalidDataException(what);
            }
        }

        static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        static bool Optional(string? value, int max) => value is null || value.Length <= max;

        if (backup.Home is { } home)
        {
            Require(Text(home.Name, ValidationRules.HomeNameMaxLength) && Text(home.TimeZoneId, 64) && Text(home.Locale, 16) && Text(home.Currency, 3) && Text(home.TariffKind, 32), "home");
        }

        Require(backup.Floors.Select(f => f.Id).Distinct().Count() == backup.Floors.Count, "floor ids");
        Require(backup.Floors.SelectMany(f => f.Rooms).Select(r => r.Id).Distinct().Count() == backup.Floors.Sum(f => f.Rooms.Count), "room ids");
        foreach (var floor in backup.Floors)
        {
            Require(Text(floor.Name, FloorRules.NameMaxLength)
                && floor.WidthCm is >= FloorRules.MinSizeCm and <= FloorRules.MaxSizeCm
                && floor.HeightCm is >= FloorRules.MinSizeCm and <= FloorRules.MaxSizeCm
                && floor.GridCm is >= FloorRules.MinGridCm and <= FloorRules.MaxGridCm, "floor");
            foreach (var room in floor.Rooms)
            {
                Require(Text(room.Name, FloorRules.NameMaxLength)
                    && Text(room.Icon, FloorRules.IconMaxLength)
                    && RoomColors.Tokens.Contains(room.ColorToken)
                    && RoomPolygonRules.Validate([.. room.Polygon.Select(p => new PointCm(p.X, p.Y))], floor.WidthCm, floor.HeightCm) is null, "room");
            }
        }

        Require(backup.Files.Select(f => f.Record.Id).Distinct().Count() == backup.Files.Count, "file ids");
        Require(backup.Integrations.Select(i => i.Id).Distinct().Count() == backup.Integrations.Count, "integration ids");
        foreach (var integration in backup.Integrations)
        {
            Require(Text(integration.ConnectorKey, DeviceLimits.ConnectorKeyMaxLength) && Text(integration.Name, DeviceLimits.NameMaxLength)
                && integration.Config.ValueKind == JsonValueKind.Object, "integration");
        }

        var integrationIds = backup.Integrations.Select(i => i.Id).ToHashSet();
        Require(backup.Devices.Select(d => d.Id).Distinct().Count() == backup.Devices.Count, "device ids");
        Require(backup.Devices.Select(d => (d.IntegrationId, d.ExternalId)).Distinct().Count() == backup.Devices.Count, "device external ids");
        foreach (var device in backup.Devices)
        {
            Require(integrationIds.Contains(device.IntegrationId)
                && Text(device.ExternalId, DeviceLimits.ExternalIdMaxLength)
                && Text(device.Name, DeviceLimits.NameMaxLength)
                && Optional(device.DeviceClass, DeviceLimits.DeviceClassMaxLength)
                && Optional(device.Manufacturer, DeviceLimits.ManufacturerMaxLength)
                && Optional(device.Model, DeviceLimits.ModelMaxLength)
                && Optional(device.Firmware, DeviceLimits.FirmwareMaxLength)
                && Optional(device.Icon, DeviceLimits.IconMaxLength)
                && device.Connections.ValueKind == JsonValueKind.Object, "device");
        }

        var deviceIds = backup.Devices.Select(d => d.Id).ToHashSet();
        Require(backup.Endpoints.Select(e => e.Id).Distinct().Count() == backup.Endpoints.Count, "endpoint ids");
        Require(backup.Endpoints.Select(e => (e.DeviceId, e.Key)).Distinct().Count() == backup.Endpoints.Count, "endpoint keys");
        foreach (var endpoint in backup.Endpoints)
        {
            Require(deviceIds.Contains(endpoint.DeviceId)
                && Text(endpoint.Key, DeviceLimits.EndpointKeyMaxLength)
                && Optional(endpoint.Name, DeviceLimits.NameMaxLength)
                && Optional(endpoint.Icon, DeviceLimits.IconMaxLength)
                && endpoint.Capabilities.Select(c => c.CapabilityId).Distinct().Count() == endpoint.Capabilities.Count
                && endpoint.Capabilities.All(c => Text(c.CapabilityId, DeviceLimits.CapabilityIdMaxLength)
                    && c.Attributes.All(a => Text(a, DeviceLimits.AttributeKeyMaxLength))), "endpoint");
        }

        Require(backup.Placements.Count(p => p.EndpointId is not null) == backup.Placements.Where(p => p.EndpointId is not null).Select(p => p.EndpointId).Distinct().Count(), "placements");
        Require(backup.Placements.All(p => (p.EndpointId is null) != (p.DeviceId is null) && double.IsFinite(p.XCm) && double.IsFinite(p.YCm)), "placement");
        Require(backup.Users.Select(u => u.UserName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == backup.Users.Count, "users");
        Require(backup.Users.All(u => Text(u.UserName, 64) && Text(u.DisplayName, 128) && Text(u.Locale, 16) && Text(u.Theme, 16)), "user");
    }

    private static T Read<T>(ZipArchive zip, string name)
    {
        var bytes = ReadBytes(zip, name);
        return JsonSerializer.Deserialize<T>(bytes, BackupEntries.Json) ?? throw new JsonException($"{name} is empty.");
    }

    private static byte[] ReadBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing.");
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"{name} is too large.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes));
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > MaxEntryBytes)
            {
                throw new InvalidDataException($"{name} is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private string? EncryptSecrets(byte[] key, Integration integration)
    {
        if (integration.SecretsProtected is null)
        {
            return null;
        }

        IntegrationSecrets secrets;
        try
        {
            secrets = secretProtector.Read(integration.SecretsProtected);
        }
        catch (CryptographicException)
        {
            return null; // the key ring cannot open them: there is nothing to export
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new SecretsRecord(secrets.Values, secrets.Devices), BackupEntries.Json);
        return BackupCipher.Encrypt(key, json, SecretsContext(integration.Id));
    }

    private static string SecretsContext(Guid integrationId) =>
        string.Create(CultureInfo.InvariantCulture, $"integration:{integrationId:D}");

    private static string FilePath(StoredFile file) => string.Create(
        CultureInfo.InvariantCulture,
        $"{BackupEntries.FilesFolder}{file.Id:D}.{file.ContentType switch { "image/png" => "png", "image/jpeg" => "jpg", _ => "webp" }}");

    private static void Write<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, BackupEntries.Json);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AppError Invalid() => AppError.Validation(ErrorCodes.BackupInvalid, "This file is not a ControlHome backup, or it is damaged.");

    private sealed record ParsedFile(FileRecord Record, byte[] Data);

    private sealed record ParsedBackup(
        BackupManifest Manifest,
        HomeRecord? Home,
        IReadOnlyList<FloorRecord> Floors,
        IReadOnlyList<ParsedFile> Files,
        IReadOnlyList<IntegrationRecord> Integrations,
        IReadOnlyList<DeviceRecord> Devices,
        IReadOnlyList<EndpointRecord> Endpoints,
        IReadOnlyList<PlacementRecord> Placements,
        IReadOnlyList<UserRecord> Users);
}
