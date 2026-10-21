using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>
/// Collects a device model while the mapper walks the profile: endpoints (catalogue capabilities with constraints, plus one
/// <c>generic</c> capability), read rules, command builders, and the properties already mapped (so the generic pass skips
/// them).
/// </summary>
internal sealed class LgModelBuilder(LgProfile profile)
{
    private readonly List<LgEndpointBuilder> _endpoints = [];
    private readonly HashSet<LgProperty> _claimed = new(ReferenceEqualityComparer.Instance);
    private readonly List<LgReadRule> _reads = [];
    private readonly Dictionary<(string, string, string), LgCommandBuilder> _commands = [];

    public LgProfile Profile => profile;

    public IReadOnlyList<LgEndpointBuilder> Endpoints => _endpoints;

    /// <summary>The endpoint with <paramref name="key"/> (created, in order, on first use).</summary>
    public LgEndpointBuilder Endpoint(string key, string? name = null)
    {
        var endpoint = _endpoints.FirstOrDefault(e => e.Key == key);
        if (endpoint is null)
        {
            endpoint = new LgEndpointBuilder(key, name);
            _endpoints.Add(endpoint);
        }

        return endpoint;
    }

    /// <summary>The properties of an endpoint, in profile order.</summary>
    public IEnumerable<LgProperty> In(string endpoint) => profile.Properties.Where(p => p.Place.EndpointKey == endpoint);

    /// <summary>
    /// A property of an endpoint by resource and key; for lists of temperature units the °C entry wins (then an entry without
    /// a unit).
    /// </summary>
    public LgProperty? Find(string endpoint, string resource, string key) =>
        In(endpoint)
            .Where(p => p.Resource == resource && p.Key == key)
            .OrderBy(p => p.Place.Selector == LgPlace.UnitSelector ? p.Place.SelectorValue == "C" ? 0 : 2 : 1)
            .FirstOrDefault(p => p.Unit != "F");

    /// <summary>The first property of an endpoint matching <paramref name="match"/>.</summary>
    public LgProperty? Find(string endpoint, Func<LgProperty, bool> match) => In(endpoint).FirstOrDefault(match);

    public bool IsClaimed(LgProperty property) => _claimed.Contains(property);

    /// <summary>Marks properties as mapped by a catalogue capability (the generic pass skips them).</summary>
    public void Claim(params LgProperty?[] properties)
    {
        foreach (var property in properties)
        {
            if (property is not null)
            {
                _claimed.Add(property);
            }
        }
    }

    public void Read(string endpoint, string capability, string attribute, LgReader read) =>
        _reads.Add(new LgReadRule(endpoint, capability, attribute, read));

    public void Command(string endpoint, string capability, string command, LgCommandBuilder build) =>
        _commands[(endpoint, capability, command)] = build;

    public LgDeviceModel Build(
        string deviceId,
        string deviceType,
        DiscoveredDevice device,
        IReadOnlyList<(string Endpoint, string Property)> energy,
        IReadOnlyDictionary<string, string> notifications) =>
        new(deviceId, deviceType, device with { Endpoints = [.. _endpoints.Where(e => !e.IsEmpty).Select(e => e.Build())] }, _reads, _commands, energy, notifications);
}

/// <summary>One endpoint under construction.</summary>
internal sealed class LgEndpointBuilder(string key, string? name)
{
    private readonly List<string> _capabilities = [];
    private readonly Dictionary<string, Dictionary<string, AttributeConstraint>> _constraints = new(StringComparer.Ordinal);
    private readonly List<AttributeDefinition> _genericAttributes = [];
    private readonly List<CommandDefinition> _genericCommands = [];

    public string Key { get; } = key;

    public string? Name { get; } = name;

    public bool IsEmpty => _capabilities.Count == 0 && _genericAttributes.Count == 0;

    public bool Has(string capabilityId) => _capabilities.Contains(capabilityId, StringComparer.Ordinal);

    public LgEndpointBuilder Add(string capabilityId)
    {
        if (!Has(capabilityId))
        {
            _capabilities.Add(capabilityId);
        }

        return this;
    }

    public LgEndpointBuilder Constrain(string capabilityId, string attributeKey, AttributeConstraint constraint)
    {
        if (!_constraints.TryGetValue(capabilityId, out var attributes))
        {
            attributes = new Dictionary<string, AttributeConstraint>(StringComparer.Ordinal);
            _constraints[capabilityId] = attributes;
        }

        attributes[attributeKey] = constraint;
        return this;
    }

    public bool HasGeneric(string key) => _genericAttributes.Any(a => a.Key == key);

    /// <summary>Adds a generic attribute under a unique key; a writable one gets a setter <c>set&lt;Key&gt;</c>. Returns (key, setter).</summary>
    public (string Key, string? Setter) AddGeneric(string preferredKey, string fallbackKey, ValueSchema schema, bool writable, AttributeCategory category = AttributeCategory.Primary)
    {
        var key = Unique(HasGeneric(preferredKey) ? fallbackKey : preferredKey);
        _genericAttributes.Add(new AttributeDefinition(key, schema, (schema as NumberSchema)?.Unit, StateClass.None, category, Recorded: false, Writable: writable));
        if (!writable)
        {
            return (key, null);
        }

        var setter = "set" + char.ToUpperInvariant(key[0]) + key[1..];
        _genericCommands.Add(new CommandDefinition(
            setter,
            [new ArgumentDefinition(key, schema, Required: true, Attribute: key)],
            ConfirmationMode.AwaitState,
            null,
            SafetyCritical: false,
            FailSafe: false));
        return (key, setter);
    }

    public DiscoveredEndpoint Build()
    {
        var bindings = _capabilities
            .Select(id => _constraints.TryGetValue(id, out var attributes) && attributes.Count > 0
                ? CapabilityBinding.Of(id, new CapabilityConstraints(attributes))
                : CapabilityBinding.Of(id))
            .ToList();
        if (_genericAttributes.Count > 0)
        {
            bindings.Add(new CapabilityBinding(
                CapabilityIds.Generic,
                null,
                new CapabilityDefinition(CapabilityIds.Generic, 1, [.. _genericAttributes], [.. _genericCommands])));
        }

        return new DiscoveredEndpoint(Key, Name, bindings);
    }

    private string Unique(string key)
    {
        var candidate = key.Length > 120 ? key[..120] : key;
        for (var i = 2; HasGeneric(candidate); i++)
        {
            candidate = $"{key}{i}";
        }

        return candidate;
    }
}

/// <summary>Value conversions between LG status values and attribute values.</summary>
internal static class LgValues
{
    public static string? Text(JsonElement? value) => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    public static double? Number(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;

    public static bool? Flag(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text => text.GetString() switch
        {
            "true" or "TRUE" or "ON" => true,
            "false" or "FALSE" or "OFF" => false,
            _ => null,
        },
        _ => null,
    };

    /// <summary>A number as a JSON node: integral values as integers (LG rejects 24.0 for an integer range on some models).</summary>
    public static JsonNode Node(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15 ? JsonValue.Create((long)Math.Round(value)) : JsonValue.Create(value);

    /// <summary>A generic attribute's value from an LG status value of <paramref name="type"/>.</summary>
    public static StateValue? Generic(LgPropertyType type, JsonElement? value) => value is not { } v ? null : type switch
    {
        LgPropertyType.Enum => Text(v) is { } text ? StateValue.String(text) : Flag(v) is { } flag ? StateValue.String(flag ? "true" : "false") : null,
        LgPropertyType.Boolean => Flag(v) is { } flag ? StateValue.Bool(flag) : null,
        LgPropertyType.Range or LgPropertyType.Number => Number(v) is { } number ? StateValue.Number(number) : null,
        LgPropertyType.String => Text(v) is { } text ? StateValue.String(text.Length > 255 ? text[..255] : text)
            : v.ValueKind == JsonValueKind.Number ? StateValue.String(v.GetRawText()) : null,
        _ => v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? StateValue.Json(v)
            : StateValue.FromJson(v) is { Kind: not StateValueKind.Null } scalar ? StateValue.String(scalar.ToString()) : null,
    };

    /// <summary>The JSON to send for a generic setter's argument (enum and string as strings, booleans as JSON booleans).</summary>
    public static JsonNode? Argument(LgPropertyType type, JsonElement argument) => type switch
    {
        LgPropertyType.Boolean => JsonValue.Create(argument.GetBoolean()),
        LgPropertyType.Range or LgPropertyType.Number => Node(argument.GetDouble()),
        LgPropertyType.Enum when argument.GetString() is "true" or "false" => JsonValue.Create(argument.GetString() == "true"),
        _ => JsonValue.Create(argument.GetString()),
    };
}
