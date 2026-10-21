using System.Globalization;
using System.Text;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>
/// One endpoint under construction: catalogue capabilities (each once, with constraints) and one <c>generic</c> capability
/// collecting the vendor attributes and commands without a catalogue mapping.
/// </summary>
internal sealed class EndpointBuilder(string key, string? name)
{
    private readonly List<string> _capabilities = [];
    private readonly Dictionary<string, Dictionary<string, AttributeConstraint>> _constraints = new(StringComparer.Ordinal);
    private readonly List<AttributeDefinition> _genericAttributes = [];
    private readonly List<CommandDefinition> _genericCommands = [];
    private readonly HashSet<string> _genericKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _genericCommandNames = new(StringComparer.Ordinal);

    public string Key { get; } = key;

    public string? Name { get; } = name;

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

    /// <summary>Narrows one attribute of a catalogue capability.</summary>
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

    /// <summary>Adds a generic attribute under a unique key derived from <paramref name="preferredKey"/>; returns the key.</summary>
    public string AddGenericAttribute(string preferredKey, ValueSchema schema, bool writable, AttributeCategory category = AttributeCategory.Primary)
    {
        var key = Naming.Unique(Naming.Limit(preferredKey, DeviceLimits.AttributeKeyMaxLength - 4), _genericKeys);
        _genericAttributes.Add(new AttributeDefinition(key, schema, (schema as NumberSchema)?.Unit, StateClass.None, category, Recorded: false, writable));
        return key;
    }

    /// <summary>Adds a generic command under a unique name derived from <paramref name="preferredName"/>; returns the name.</summary>
    public string AddGenericCommand(string preferredName, IReadOnlyList<ArgumentDefinition> arguments, ConfirmationMode confirmation)
    {
        var name = Naming.Unique(Naming.Limit(preferredName, 60), _genericCommandNames);
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

/// <summary>Keys and names derived from SmartThings ids.</summary>
internal static class Naming
{
    /// <summary>The part of a capability id after its namespace: <c>samsungce.washerDelayEnd</c> → <c>washerDelayEnd</c>.</summary>
    public static string ShortCapability(string capabilityId)
    {
        var dot = capabilityId.LastIndexOf('.');
        return dot >= 0 && dot < capabilityId.Length - 1 ? capabilityId[(dot + 1)..] : capabilityId;
    }

    /// <summary>
    /// A generic attribute key for <paramref name="attribute"/> of <paramref name="capabilityId"/>, readable and stable:
    /// the attribute name when it already names the capability (<c>custom.washerSpinLevel</c>.<c>washerSpinLevel</c>,
    /// <c>tvChannel</c>.<c>tvChannelName</c>), when the capability starts with it (<c>accelerationSensor</c>.<c>acceleration</c>)
    /// or ends with it and it is specific (<c>thermostatCoolingSetpoint</c>.<c>coolingSetpoint</c>); the capability name
    /// when it ends with a one-word attribute (<c>samsungim.hueSyncMode</c>.<c>mode</c> → <c>hueSyncMode</c>); else
    /// capability + attribute (<c>samsungce.washerDelayEnd</c>.<c>remainingTime</c> → <c>washerDelayEndRemainingTime</c>).
    /// </summary>
    public static string GenericKey(string capabilityId, string attribute)
    {
        var capability = CamelCase(ShortCapability(capabilityId));
        var name = CamelCase(attribute);
        if (name.StartsWith(capability, StringComparison.OrdinalIgnoreCase) || capability.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        if (capability.EndsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return name.Any(char.IsUpper) ? name : capability;
        }

        return capability + Pascal(name);
    }

    /// <summary>camelCase letters and digits only (<c>DeviceWatch-DeviceStatus</c> → <c>deviceWatchDeviceStatus</c>); never empty.</summary>
    public static string CamelCase(string name)
    {
        var builder = new StringBuilder();
        var upperNext = false;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                upperNext = builder.Length > 0;
                continue;
            }

            builder.Append(builder.Length == 0 ? char.ToLowerInvariant(c) : upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        if (builder.Length == 0)
        {
            return "value";
        }

        return char.IsAsciiDigit(builder[0]) ? "v" + builder : builder.ToString();
    }

    public static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>A lower-case endpoint key from a component id: letters, digits, <c>_</c> and <c>-</c> only, at most 64 characters.</summary>
    public static string EndpointKey(string componentId)
    {
        var builder = new StringBuilder();
        foreach (var c in componentId.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        var key = builder.ToString().Trim('_');
        return key.Length == 0 ? "component" : Limit(key, DeviceLimits.EndpointKeyMaxLength);
    }

    public static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    /// <summary>A key not in <paramref name="taken"/>: <paramref name="key"/>, then <c>key2</c>, <c>key3</c>, …</summary>
    public static string Unique(string key, ISet<string> taken)
    {
        if (taken.Add(key))
        {
            return key;
        }

        for (var i = 2; ; i++)
        {
            var candidate = key + i.ToString(CultureInfo.InvariantCulture);
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
