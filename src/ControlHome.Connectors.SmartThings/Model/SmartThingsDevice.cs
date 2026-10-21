using System.Text.Json;

namespace ControlHome.Connectors.SmartThings.Model;

/// <summary>A capability a component implements (<c>{"id":"switchLevel","version":1}</c>).</summary>
internal sealed record CapabilityRef(string Id, int Version);

/// <summary>A device category (<c>{"name":"Light","categoryType":"manufacturer"}</c>); the user's one wins.</summary>
internal sealed record DeviceCategory(string Name, bool ChosenByUser);

/// <summary>One component of a device (<c>main</c>, <c>freezer</c>, <c>1</c>, …): it becomes an endpoint.</summary>
internal sealed record DeviceComponent(string Id, string? Label, IReadOnlyList<CapabilityRef> Capabilities, IReadOnlyList<DeviceCategory> Categories)
{
    public bool Has(string capabilityId) => Capabilities.Any(c => string.Equals(c.Id, capabilityId, StringComparison.Ordinal));
}

/// <summary>
/// A device as <c>GET /v1/devices</c> describes it. Vendor payloads drift: every member is optional except the id, and
/// unknown members are ignored.
/// </summary>
internal sealed record SmartThingsDevice(
    string DeviceId,
    string? Label,
    string? Name,
    string? ManufacturerName,
    string? DeviceManufacturerCode,
    string? PresentationId,
    string? LocationId,
    string? RoomId,
    string? ParentDeviceId,
    string? HealthState,
    string? ViperManufacturer,
    string? ViperModel,
    string? ViperFirmware,
    string? OcfManufacturer,
    IReadOnlyList<DeviceComponent> Components)
{
    public DeviceComponent? Main => Components.FirstOrDefault(c => c.Id == "main") ?? Components.FirstOrDefault();

    /// <summary>The device described by an item of <c>GET /v1/devices</c>, or <c>null</c> without a device id.</summary>
    public static SmartThingsDevice? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || item.Str("deviceId") is not { Length: > 0 } id)
        {
            return null;
        }

        var components = new List<DeviceComponent>();
        foreach (var component in item.Array("components"))
        {
            if (component.Str("id") is not { Length: > 0 } componentId)
            {
                continue;
            }

            var capabilities = component.Array("capabilities")
                .Select(c => c.Str("id") is { Length: > 0 } capability
                    ? new CapabilityRef(capability, (int)(c.Long("version") ?? 1))
                    : null)
                .OfType<CapabilityRef>()
                .DistinctBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            var categories = component.Array("categories")
                .Select(c => c.Str("name") is { Length: > 0 } name
                    ? new DeviceCategory(name, string.Equals(c.Str("categoryType"), "user", StringComparison.OrdinalIgnoreCase))
                    : null)
                .OfType<DeviceCategory>()
                .ToList();
            components.Add(new DeviceComponent(componentId, component.Str("label"), capabilities, categories));
        }

        var viper = item.Get("viper");
        var ocf = item.Get("ocf");
        return new SmartThingsDevice(
            id,
            item.Str("label"),
            item.Str("name"),
            item.Str("manufacturerName"),
            item.Str("deviceManufacturerCode"),
            item.Str("presentationId"),
            item.Str("locationId"),
            item.Str("roomId"),
            item.Str("parentDeviceId"),
            item.Get("healthState").Str("state"),
            viper.Str("manufacturerName"),
            viper.Str("modelName"),
            viper.Str("swVersion"),
            ocf.Str("manufacturerName"),
            components);
    }
}

/// <summary>One attribute's state (<c>{"value":…,"unit":"C","timestamp":"…"}</c>).</summary>
internal sealed record StAttribute(JsonElement Value, string? Unit, DateTimeOffset? Timestamp)
{
    public bool HasValue => Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public string? Text => HasValue ? Json.AsText(Value) : null;

    public double? Number => HasValue ? Json.Number(Value) : null;

    public static StAttribute? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = element.TryGetProperty("value", out var v) ? v.Clone() : default;
        return new StAttribute(value, element.Str("unit"), Json.Time(element, "timestamp"));
    }
}

/// <summary>
/// A device's state: component id → capability id → attribute → <see cref="StAttribute"/>. Built from the per-capability
/// <c>status</c> of a <c>GET /v1/devices?includeStatus=true</c> item or from <c>GET /v1/devices/{id}/status</c>.
/// </summary>
internal sealed class DeviceStatus
{
    public static DeviceStatus Empty { get; } = new(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, StAttribute>>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, StAttribute>>> _components;

    private DeviceStatus(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, StAttribute>>> components) =>
        _components = components;

    public IEnumerable<string> ComponentIds => _components.Keys;

    /// <summary>The capability's attributes on a component (empty when unknown).</summary>
    public IReadOnlyDictionary<string, StAttribute> Of(string componentId, string capabilityId) =>
        _components.TryGetValue(componentId, out var capabilities) && capabilities.TryGetValue(capabilityId, out var attributes)
            ? attributes
            : _none;

    /// <summary>One attribute's state, or <c>null</c>.</summary>
    public StAttribute? Get(string componentId, string capabilityId, string attribute) =>
        Of(componentId, capabilityId).GetValueOrDefault(attribute);

    /// <summary>The status embedded in a device listing item (<c>components[].capabilities[].status</c>).</summary>
    public static DeviceStatus FromListing(JsonElement item)
    {
        var components = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, StAttribute>>>(StringComparer.Ordinal);
        foreach (var component in item.Array("components"))
        {
            if (component.Str("id") is not { Length: > 0 } componentId)
            {
                continue;
            }

            var capabilities = new Dictionary<string, IReadOnlyDictionary<string, StAttribute>>(StringComparer.Ordinal);
            foreach (var capability in component.Array("capabilities"))
            {
                if (capability.Str("id") is { Length: > 0 } capabilityId && capability.Get("status") is { ValueKind: JsonValueKind.Object } status)
                {
                    capabilities[capabilityId] = Attributes(status);
                }
            }

            components[componentId] = capabilities;
        }

        return new DeviceStatus(components);
    }

    /// <summary>The answer of <c>GET /v1/devices/{id}/status</c> (<c>{"components":{…}}</c>).</summary>
    public static DeviceStatus Parse(JsonElement document)
    {
        var components = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, StAttribute>>>(StringComparer.Ordinal);
        foreach (var component in document.Members("components"))
        {
            var capabilities = new Dictionary<string, IReadOnlyDictionary<string, StAttribute>>(StringComparer.Ordinal);
            if (component.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var capability in component.Value.EnumerateObject())
                {
                    if (capability.Value.ValueKind == JsonValueKind.Object)
                    {
                        capabilities[capability.Name] = Attributes(capability.Value);
                    }
                }
            }

            components[component.Name] = capabilities;
        }

        return new DeviceStatus(components);
    }

    private static readonly IReadOnlyDictionary<string, StAttribute> _none = new Dictionary<string, StAttribute>();

    private static Dictionary<string, StAttribute> Attributes(JsonElement status)
    {
        var attributes = new Dictionary<string, StAttribute>(StringComparer.Ordinal);
        foreach (var attribute in status.EnumerateObject())
        {
            if (StAttribute.Parse(attribute.Value) is { } state)
            {
                attributes[attribute.Name] = state;
            }
        }

        return attributes;
    }
}
