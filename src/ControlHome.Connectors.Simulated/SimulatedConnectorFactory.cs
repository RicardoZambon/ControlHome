using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Simulated;

/// <summary>Settings bound from configuration section <see cref="SectionName"/> (e.g. <c>Simulated__Seed=7</c>).</summary>
public sealed class SimulatedConnectorOptions
{
    public const string SectionName = "Simulated";

    /// <summary>The seed used when an integration's config has none.</summary>
    public int Seed { get; set; } = SimulatedSettings.DefaultSeed;
}

/// <summary>
/// The Simulated connector (key <c>simulated</c>): an in-process home whose devices behave deterministically for a given
/// seed and clock. Config: <c>{ "preset": "basic" | "demo", "seed": 42 }</c> (both optional).
/// </summary>
public sealed class SimulatedConnectorFactory(SimulatedConnectorOptions? options = null) : IConnectorFactory
{
    public const string Key = "simulated";

    private readonly int _defaultSeed = options?.Seed ?? SimulatedSettings.DefaultSeed;

    public IConnectorDescriptor Descriptor => SimulatedConnectorDescriptor.Instance;

    /// <summary>No setup flow yet (a form + "press the button" flow arrives with the integrations UI); create from config.</summary>
    public IConnectorFlow? CreateSetupFlow() => null;

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new SimulatedRuntime(SimulatedSettings.Parse(instance.Config, _defaultSeed));
    }
}

/// <summary>The Simulated connector's descriptor.</summary>
public sealed class SimulatedConnectorDescriptor : IConnectorDescriptor
{
    public static SimulatedConnectorDescriptor Instance { get; } = new();

    public string Key => SimulatedConnectorFactory.Key;

    public LocalizedText DisplayName { get; } = new("Simulated home", "Casa simulada");

    public string Icon => "flask-conical";

    public ConnectorCategory Category => ConnectorCategory.Virtual;

    public IReadOnlyList<FieldSchema> ConfigFields { get; } =
    [
        new FieldSchema(
            SimulatedSettings.PresetField,
            new LocalizedText("Devices", "Dispositivos"),
            new EnumSchema([SimulatedSettings.BasicPreset, SimulatedSettings.DemoPreset]),
            Required: false,
            Secret: false,
            new LocalizedText(
                "basic: a light, a plug, an AC, a door sensor and a lock. demo: the demo home.",
                "basic: uma luz, uma tomada, um ar-condicionado, um sensor de porta e uma fechadura. demo: a casa de demonstração.")),
        new FieldSchema(
            SimulatedSettings.SeedField,
            new LocalizedText("Random seed", "Semente aleatória"),
            new NumberSchema(0, int.MaxValue, 1, null, Integer: true),
            Required: false,
            Secret: false,
            new LocalizedText("The same seed always produces the same values.", "A mesma semente sempre produz os mesmos valores.")),
    ];

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { PushInventory = true };
}

/// <summary>A Simulated integration's parsed configuration.</summary>
public sealed record SimulatedSettings(string Preset, int Seed)
{
    public const string PresetField = "preset";
    public const string SeedField = "seed";
    public const string BasicPreset = "basic";

    /// <summary>The demo home (currently the basic devices; the full ~20-device demo arrives with the demo seed).</summary>
    public const string DemoPreset = "demo";

    public const int DefaultSeed = 42;

    /// <exception cref="ArgumentException">The config does not match the descriptor's config fields.</exception>
    public static SimulatedSettings Parse(JsonElement config, int defaultSeed = DefaultSeed)
    {
        var validation = FieldSchemaValidator.Validate(SimulatedConnectorDescriptor.Instance.ConfigFields, config);
        if (!validation.IsValid)
        {
            var problems = string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "config"}: {e.Kind}"));
            throw new ArgumentException($"Invalid Simulated configuration ({problems}).", nameof(config));
        }

        var preset = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(PresetField, out var presetValue)
            && presetValue.ValueKind == JsonValueKind.String
                ? presetValue.GetString()!
                : BasicPreset;
        var seed = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty(SeedField, out var seedValue)
            && seedValue.ValueKind == JsonValueKind.Number
                ? (int)seedValue.GetDouble()
                : defaultSeed;
        return new SimulatedSettings(preset, seed);
    }
}
