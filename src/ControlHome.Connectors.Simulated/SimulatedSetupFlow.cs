using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Simulated;

/// <summary>
/// The Simulated connector's flow, a stand-in for a real hub's pairing: a form with the settings (preset, seed,
/// simulate expired credentials) → a progress step "press the button on the hub" that completes on the first poll at least
/// <see cref="PairingDelay"/> after it started (the simulated button presses itself) and aborts after
/// <see cref="PairingTimeout"/> → done, with a fresh hub token as the integration's secret. Started for an existing
/// integration it re-authorizes it: the form is filled with its settings (expired credentials cleared).
/// </summary>
internal sealed class SimulatedSetupFlow(int defaultSeed) : IConnectorFlow
{
    public const string SettingsStep = "settings";
    public const string PairingStep = "pairing";

    /// <summary>The secret the flow hands out (stored encrypted with the integration, never in its config).</summary>
    public const string TokenSecret = "hubToken";

    public static readonly TimeSpan PairingDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PairingTimeout = TimeSpan.FromMinutes(1);

    private FlowContext? _context;
    private JsonElement? _config;
    private DateTimeOffset _pairingStarted;

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var values = context.Existing is { } existing
            ? Normalize(SimulatedSettings.Parse(existing.Config, defaultSeed) with { SimulateReauth = false })
            : Normalize(new SimulatedSettings(SimulatedSettings.BasicPreset, defaultSeed));
        return Task.FromResult<FlowStep>(Form(values, new Dictionary<string, LocalizedText>()));
    }

    public Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        return Task.FromResult(stepId switch
        {
            SettingsStep => SubmitSettings(context, input),
            PairingStep => Poll(context),
            _ => new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo.")),
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private FlowStep SubmitSettings(FlowContext context, JsonElement input)
    {
        var validation = FieldSchemaValidator.Validate(SimulatedConnectorDescriptor.Instance.ConfigFields, input);
        if (!validation.IsValid)
        {
            var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
            foreach (var error in validation.Errors)
            {
                errors.TryAdd(
                    error.Argument ?? SimulatedSettings.PresetField,
                    error.Kind == ValueErrorKind.Unknown
                        ? new LocalizedText("Unknown field.", "Campo desconhecido.")
                        : new LocalizedText("This value is not valid.", "Este valor não é válido."));
            }

            return Form(input.ValueKind == JsonValueKind.Object ? input : null, errors);
        }

        _config = Normalize(SimulatedSettings.Parse(input, defaultSeed));
        _pairingStarted = context.Time.GetUtcNow();
        return Pairing(PairingTimeout);
    }

    private FlowStep Poll(FlowContext context)
    {
        if (_config is not { } config)
        {
            return new AbortStep("not_started", new LocalizedText("Pairing has not started.", "O pareamento não foi iniciado."));
        }

        var elapsed = context.Time.GetUtcNow() - _pairingStarted;
        if (elapsed >= PairingTimeout)
        {
            return new AbortStep(
                "timeout",
                new LocalizedText("The hub's button was not pressed in time. Start again.", "O botão do hub não foi pressionado a tempo. Comece de novo."));
        }

        if (elapsed < PairingDelay)
        {
            return Pairing(PairingTimeout - elapsed);
        }

        var preset = config.GetProperty(SimulatedSettings.PresetField).GetString();
        var name = context.Existing?.Name ?? (preset == SimulatedSettings.DemoPreset ? "Demo home" : "Simulated home");
        return new DoneStep(name, config)
        {
            Secrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TokenSecret] = "sim-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            },
        };
    }

    private static FormStep Form(JsonElement? values, IReadOnlyDictionary<string, LocalizedText> errors) =>
        new(
            SettingsStep,
            new LocalizedText("Simulated home", "Casa simulada"),
            new LocalizedText(
                "Pick the simulated devices. The demo preset is the full demo home.",
                "Escolha os dispositivos simulados. O conjunto demo é a casa de demonstração completa."),
            SimulatedConnectorDescriptor.Instance.ConfigFields)
        {
            Values = values,
            Errors = errors,
        };

    private static ProgressStep Pairing(TimeSpan remaining)
    {
        var seconds = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return new ProgressStep(
            PairingStep,
            new LocalizedText("Press the button on the hub", "Pressione o botão do hub"),
            new LocalizedText(
                $"Press the pairing button on the simulated hub within {seconds} s. (It presses itself in a moment.)",
                $"Pressione o botão de pareamento do hub simulado em até {seconds} s. (Ele se pressiona sozinho em instantes.)"),
            remaining);
    }

    private static JsonElement Normalize(SimulatedSettings settings) => JsonSerializer.SerializeToElement(new JsonObject
    {
        [SimulatedSettings.PresetField] = settings.Preset,
        [SimulatedSettings.SeedField] = settings.Seed,
        [SimulatedSettings.SimulateReauthField] = settings.SimulateReauth,
    });
}
