using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Integrations;

/// <summary>
/// The flow of a connector without one of its own: a single form with its config fields → done (secret fields go to the
/// secrets, never the config). For an existing integration the form starts with its settings, and a secret field left empty
/// keeps the stored value.
/// </summary>
internal sealed class ConfigFieldsFlow(IConnectorDescriptor descriptor) : IConnectorFlow
{
    public const string ConfigStep = "config";

    private FlowContext? _context;

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var values = context.Existing is { } existing ? IntegrationService.Split(descriptor.ConfigFields, existing.Config).Plain : null;
        return Task.FromResult<FlowStep>(Form(values is null ? null : JsonSerializer.SerializeToElement(values), new Dictionary<string, LocalizedText>()));
    }

    public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        if (!string.Equals(stepId, ConfigStep, StringComparison.Ordinal))
        {
            return new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo."));
        }

        var (plain, secrets) = IntegrationService.Split(descriptor.ConfigFields, input);
        if (context.Existing is { } existing)
        {
            // A secret left empty keeps the stored one (admins never see it).
            foreach (var field in descriptor.ConfigFields.Where(f => f.Secret && !secrets.ContainsKey(f.Key)))
            {
                if (await existing.Secrets.GetAsync(field.Key, cancellationToken).ConfigureAwait(false) is { } stored)
                {
                    secrets[field.Key] = stored;
                }
            }
        }

        var complete = plain.DeepClone().AsObject();
        foreach (var (key, value) in secrets)
        {
            complete[key] = value;
        }

        var validation = FieldSchemaValidator.Validate(descriptor.ConfigFields, JsonSerializer.SerializeToElement(complete));
        if (!validation.IsValid)
        {
            var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
            foreach (var error in validation.Errors)
            {
                errors.TryAdd(error.Argument ?? descriptor.ConfigFields.FirstOrDefault()?.Key ?? "config", Message(error.Kind));
            }

            return Form(JsonSerializer.SerializeToElement(plain), errors);
        }

        return new DoneStep(context.Existing?.Name ?? descriptor.DisplayName.En, JsonSerializer.SerializeToElement(plain)) { Secrets = secrets };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private FormStep Form(JsonElement? values, IReadOnlyDictionary<string, LocalizedText> errors) =>
        new(ConfigStep, descriptor.DisplayName, null, descriptor.ConfigFields) { Values = values, Errors = errors };

    private static LocalizedText Message(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => new LocalizedText("Required.", "Obrigatório."),
        ValueErrorKind.Unknown => new LocalizedText("Unknown field.", "Campo desconhecido."),
        ValueErrorKind.OutOfRange => new LocalizedText("Out of range.", "Fora do intervalo permitido."),
        ValueErrorKind.TooLong => new LocalizedText("Too long.", "Longo demais."),
        _ => new LocalizedText("This value is not valid.", "Este valor não é válido."),
    };
}
