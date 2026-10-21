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
/// seed and clock. Config: <c>{ "preset": "basic" | "demo", "language": "en" | "pt-BR", "seed": 42, "simulateReauth": false }</c>
/// (all optional; the language names the devices and the rooms they suggest).
/// Setup runs through <see cref="SimulatedSetupFlow"/> (a form, then a "press the button" progress step); the same flow
/// re-authorizes an existing integration. Its devices follow from the config alone (<see cref="IStaticInventorySource"/>),
/// so the demo home can be seeded with them already placed.
/// </summary>
public sealed class SimulatedConnectorFactory(SimulatedConnectorOptions? options = null) : IConnectorFactory, IStaticInventorySource
{
    public const string Key = "simulated";

    private readonly int _defaultSeed = options?.Seed ?? SimulatedSettings.DefaultSeed;

    public IConnectorDescriptor Descriptor => SimulatedConnectorDescriptor.Instance;

    public IConnectorFlow? CreateSetupFlow() => new SimulatedSetupFlow(_defaultSeed);

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new SimulatedRuntime(SimulatedSettings.Parse(instance.Config, _defaultSeed));
    }

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IReadOnlyList<DiscoveredDevice> DescribeDevices(JsonElement config)
    {
        var settings = SimulatedSettings.Parse(config, _defaultSeed);
        return [.. new SimulatedHome(settings).Devices.Select(d => d.Describe(settings.Language))];
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

    public IReadOnlyList<FieldSchema> ConfigFields { get; } = CreateConfigFields();

    private static FieldSchema[] CreateConfigFields()
    {
        // The counts come from the presets themselves, so the help never drifts from what gets imported.
        var basic = new SimulatedHome(new SimulatedSettings(SimulatedSettings.BasicPreset, SimulatedSettings.DefaultSeed)).Devices.Count;
        var demo = new SimulatedHome(new SimulatedSettings(SimulatedSettings.DemoPreset, SimulatedSettings.DefaultSeed)).Devices.Count;
        return
        [
            new FieldSchema(
                SimulatedSettings.PresetField,
                new LocalizedText("Devices", "Dispositivos"),
                new EnumSchema([SimulatedSettings.BasicPreset, SimulatedSettings.DemoPreset]),
                Required: false,
                Secret: false,
                new LocalizedText(
                    $"A few devices ({basic}) to try the controls, or the whole demo home: {demo} devices of every kind.",
                    $"Alguns dispositivos ({basic}) para experimentar os controles, ou a casa de demonstração inteira: {demo} dispositivos de todo tipo.")),
            new FieldSchema(
                SimulatedSettings.LanguageField,
                new LocalizedText("Device names in", "Nomes dos dispositivos em"),
                new EnumSchema(SimulatedNames.Languages),
                Required: false,
                Secret: false,
                new LocalizedText(
                    "The language the devices and their rooms are named in (the home's, unless changed).",
                    "O idioma dos nomes dos dispositivos e dos cômodos deles (o da casa, se não for mudado)."))
            {
                Advanced = true,
            },
            new FieldSchema(
                SimulatedSettings.SeedField,
                new LocalizedText("Random seed", "Semente aleatória"),
                new NumberSchema(0, int.MaxValue, 1, null, Integer: true),
                Required: false,
                Secret: false,
                new LocalizedText("The same seed always produces the same values.", "A mesma semente sempre produz os mesmos valores."))
            {
                Advanced = true,
            },
            new FieldSchema(
                SimulatedSettings.SimulateReauthField,
                new LocalizedText("Simulate expired credentials", "Simular credenciais expiradas"),
                BoolSchema.Instance,
                Required: false,
                Secret: false,
                new LocalizedText(
                    "For testing: the hub rejects its token, so the integration needs you to sign in again and reports no devices until then.",
                    "Para testes: o hub recusa o token, então a integração precisa que você entre de novo e não informa dispositivos até lá."))
            {
                Advanced = true,
            },
        ];
    }

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { SetupFlow = true, Reauth = true, PushInventory = true };
}

/// <summary>A Simulated integration's parsed configuration.</summary>
/// <param name="Language">The language the devices are named in: <c>en</c> or <c>pt-BR</c>.</param>
public sealed record SimulatedSettings(string Preset, int Seed, bool SimulateReauth = false, string Language = "en")
{
    public const string PresetField = "preset";
    public const string LanguageField = "language";
    public const string SeedField = "seed";
    public const string SimulateReauthField = "simulateReauth";
    public const string BasicPreset = "basic";

    /// <summary>The demo home: the basic five devices plus fifteen more (see <c>SimulatedDemoDevices</c>).</summary>
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

        var isObject = config.ValueKind == JsonValueKind.Object;
        var preset = isObject && config.TryGetProperty(PresetField, out var presetValue) && presetValue.ValueKind == JsonValueKind.String
            ? presetValue.GetString()!
            : BasicPreset;
        var seed = isObject && config.TryGetProperty(SeedField, out var seedValue) && seedValue.ValueKind == JsonValueKind.Number
            ? (int)seedValue.GetDouble()
            : defaultSeed;
        var reauth = isObject && config.TryGetProperty(SimulateReauthField, out var reauthValue) && reauthValue.ValueKind == JsonValueKind.True;
        var language = isObject && config.TryGetProperty(LanguageField, out var languageValue) && languageValue.ValueKind == JsonValueKind.String
            ? languageValue.GetString()!
            : SimulatedNames.English;
        return new SimulatedSettings(preset, seed, reauth, language);
    }
}
