using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>
/// Assembles a device's endpoints: catalogue capabilities (each at most once per endpoint, with constraints) and one
/// <c>generic</c> capability per endpoint collecting the vendor properties without a catalogue mapping.
/// </summary>
internal sealed class DeviceBuilder
{
    public const string Main = "main";

    private readonly List<EndpointBuilder> _endpoints = [];

    /// <summary>The endpoint with <paramref name="key"/> (created, in order, on first use).</summary>
    public EndpointBuilder Endpoint(string key, string? name = null)
    {
        var endpoint = _endpoints.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (endpoint is null)
        {
            endpoint = new EndpointBuilder(key, name);
            _endpoints.Add(endpoint);
        }

        return endpoint;
    }

    /// <summary>
    /// Where to put another instance of <paramref name="capabilityId"/>: <paramref name="preferred"/> while it does not have
    /// it yet, else the endpoint <paramref name="fallbackKey"/> (named <paramref name="fallbackName"/>).
    /// </summary>
    public EndpointBuilder Place(string capabilityId, string preferred, string fallbackKey, string? fallbackName)
    {
        var endpoint = Endpoint(preferred);
        return endpoint.Has(capabilityId) ? Endpoint(UniqueEndpointKey(fallbackKey, capabilityId), fallbackName) : endpoint;
    }

    /// <summary>Every endpoint with at least one capability, in creation order.</summary>
    public IReadOnlyList<DiscoveredEndpoint> Build() => [.. _endpoints.Where(e => !e.IsEmpty).Select(e => e.Build())];

    private string UniqueEndpointKey(string key, string capabilityId)
    {
        var candidate = key;
        for (var i = 2; _endpoints.Any(e => e.Key == candidate && e.Has(capabilityId)); i++)
        {
            candidate = $"{key}_{i}";
        }

        return candidate;
    }
}

/// <summary>One endpoint under construction.</summary>
internal sealed class EndpointBuilder(string key, string? name)
{
    private readonly List<string> _capabilities = [];
    private readonly Dictionary<string, Dictionary<string, AttributeConstraint>> _constraints = new(StringComparer.Ordinal);
    private readonly List<AttributeDefinition> _genericAttributes = [];
    private readonly List<CommandDefinition> _genericCommands = [];
    private readonly HashSet<string> _genericKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _genericCommandNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _setters = new(StringComparer.Ordinal);

    public string Key { get; } = key;

    public string? Name { get; set; } = name;

    public bool IsEmpty => _capabilities.Count == 0 && _genericAttributes.Count == 0 && _genericCommands.Count == 0;

    public bool Has(string capabilityId) => _capabilities.Contains(capabilityId, StringComparer.Ordinal);

    /// <summary>Adds a catalogue capability (no-op when present).</summary>
    public EndpointBuilder Add(string capabilityId)
    {
        if (!Has(capabilityId))
        {
            _capabilities.Add(capabilityId);
        }

        return this;
    }

    /// <summary>Narrows one attribute of a catalogue capability already added.</summary>
    public EndpointBuilder Constrain(string capabilityId, string attributeKey, AttributeConstraint constraint)
    {
        if (!_constraints.TryGetValue(capabilityId, out var attributes))
        {
            attributes = new Dictionary<string, AttributeConstraint>(StringComparer.Ordinal);
            _constraints[capabilityId] = attributes;
        }

        attributes[attributeKey] = constraint;
        return this;
    }

    /// <summary>
    /// Adds a generic attribute (a unique key derived from <paramref name="preferredKey"/>); a writable one also gets a
    /// setter command <c>set&lt;Key&gt;</c> whose only argument is bound to it. Returns the key used.
    /// </summary>
    public string AddGeneric(
        string preferredKey,
        ValueSchema schema,
        StateClass stateClass = StateClass.None,
        AttributeCategory category = AttributeCategory.Primary,
        bool recorded = false,
        bool writable = false)
    {
        var key = Naming.Unique(preferredKey, _genericKeys);
        var unit = (schema as NumberSchema)?.Unit;
        _genericAttributes.Add(new AttributeDefinition(key, schema, unit, stateClass, category, recorded, writable));
        if (writable)
        {
            _setters[key] = AddGenericCommand(
                "set" + char.ToUpperInvariant(key[0]) + key[1..],
                [new ArgumentDefinition(key, schema, Required: true, Attribute: key)],
                ConfirmationMode.AwaitState);
        }

        return key;
    }

    /// <summary>The setter command of a writable generic attribute added with <see cref="AddGeneric"/>.</summary>
    public string SetterOf(string attributeKey) => _setters[attributeKey];

    /// <summary>Adds a generic command (a unique name derived from <paramref name="preferredName"/>); returns the name used.</summary>
    public string AddGenericCommand(string preferredName, IReadOnlyList<ArgumentDefinition> arguments, ConfirmationMode confirmation)
    {
        var name = Naming.Unique(preferredName, _genericCommandNames);
        _genericCommands.Add(new CommandDefinition(name, arguments, confirmation, null, SafetyCritical: false, FailSafe: false));
        return name;
    }

    public DiscoveredEndpoint Build()
    {
        var bindings = _capabilities
            .Select(id => _constraints.TryGetValue(id, out var attributes) && attributes.Count > 0
                ? CapabilityBinding.Of(id, new CapabilityConstraints(attributes))
                : CapabilityBinding.Of(id))
            .ToList();
        if (_genericAttributes.Count > 0 || _genericCommands.Count > 0)
        {
            bindings.Add(new CapabilityBinding(
                CapabilityIds.Generic,
                null,
                new CapabilityDefinition(CapabilityIds.Generic, 1, [.. _genericAttributes], [.. _genericCommands])));
        }

        return new DiscoveredEndpoint(Key, Name, bindings);
    }
}
