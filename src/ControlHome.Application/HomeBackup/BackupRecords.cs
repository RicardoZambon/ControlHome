using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHome.Application.HomeBackup;

// The JSON documents of a backup archive (schema version 1). Ids of integrations, devices, endpoints and files are kept
// on import; floors and rooms get new ids (references are remapped).

/// <summary>The entry names of a backup archive.</summary>
internal static class BackupEntries
{
    public const string Manifest = "manifest.json";
    public const string Home = "home.json";
    public const string Floors = "floors.json";
    public const string Files = "files.json";
    public const string Integrations = "integrations.json";
    public const string Devices = "devices.json";
    public const string Endpoints = "endpoints.json";
    public const string Placements = "placements.json";
    public const string Users = "users.json";
    public const string FilesFolder = "files/";

    /// <summary>The JSON documents (file records must name other entries).</summary>
    public static readonly IReadOnlyList<string> Documents = [Manifest, Home, Floors, Files, Integrations, Devices, Endpoints, Placements, Users];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

internal sealed record HomeRecord(
    string Name,
    string TimeZoneId,
    double? Latitude,
    double? Longitude,
    string Locale,
    string Currency,
    string TariffKind,
    decimal? PricePerKwh);

internal sealed record FloorRecord(
    int Id,
    string Name,
    int Level,
    int WidthCm,
    int HeightCm,
    int GridCm,
    BackgroundRecord? Background,
    IReadOnlyList<RoomRecord> Rooms);

internal sealed record BackgroundRecord(Guid FileId, double Opacity, double OffsetXCm, double OffsetYCm, double CmPerPx);

internal sealed record RoomRecord(int Id, string Name, string Icon, string ColorToken, int SortOrder, IReadOnlyList<PointRecord> Polygon);

internal sealed record PointRecord(double X, double Y);

/// <param name="Path">The entry holding the bytes (<c>files/&lt;id&gt;.&lt;ext&gt;</c>).</param>
internal sealed record FileRecord(
    Guid Id,
    string Purpose,
    string ContentType,
    long Length,
    string Sha256,
    int WidthPx,
    int HeightPx,
    DateTimeOffset CreatedAtUtc,
    string Path);

/// <param name="Secrets">The integration's secrets (JSON of values and per-device values), encrypted (see <see cref="BackupCipher"/>); <c>null</c> without.</param>
internal sealed record IntegrationRecord(
    Guid Id,
    string ConnectorKey,
    string Name,
    JsonElement Config,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    string? Secrets);

/// <summary>The clear-text form of <see cref="IntegrationRecord.Secrets"/> (only ever inside the encrypted payload).</summary>
internal sealed record SecretsRecord(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Devices);

internal sealed record DeviceRecord(
    Guid Id,
    Guid IntegrationId,
    string ExternalId,
    Guid? ParentDeviceId,
    string Name,
    string? DeviceClass,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    JsonElement Connections,
    bool IsOrphaned,
    int? RoomId,
    string? Icon,
    DateTimeOffset CreatedAtUtc);

internal sealed record EndpointRecord(
    Guid Id,
    Guid DeviceId,
    string Key,
    string? Name,
    int? RoomId,
    string? Icon,
    bool Hidden,
    bool Favorite,
    int SortOrder,
    IReadOnlyList<CapabilityRecord> Capabilities);

internal sealed record CapabilityRecord(
    string CapabilityId,
    int CapabilityVersion,
    JsonElement? Constraints,
    JsonElement? GenericDefinition,
    bool Retired,
    IReadOnlyList<string> Attributes);

internal sealed record PlacementRecord(int FloorId, Guid? EndpointId, Guid? DeviceId, double XCm, double YCm);

internal sealed record UserRecord(string UserName, string DisplayName, string Role, string Locale, string Theme, bool IsDisabled);
