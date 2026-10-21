using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// A step machine that sets up (or re-authorizes, pairs, permits joining for) an integration. The host keeps a flow in
/// memory while the user goes through its steps (15 min), renders each <see cref="FlowStep"/> and sends the user's input
/// back with <see cref="ContinueAsync"/> until a <see cref="DoneStep"/> or <see cref="AbortStep"/>. Declared in 1.2,
/// driven by the integrations UI from 1.3.
/// </summary>
public interface IConnectorFlow : IAsyncDisposable
{
    /// <summary>The first step.</summary>
    Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The next step after the user answered step <paramref name="stepId"/>: a form's values or a choice
    /// (<c>{"choice":"key"}</c>) as a JSON object; an empty object to poll a <see cref="ProgressStep"/> or to confirm an
    /// <see cref="ExternalUrlStep"/> without paste-back. Returning the same step again (e.g. a form with
    /// <see cref="FormStep.Errors"/>, or a progress step still waiting) is allowed.
    /// </summary>
    Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken);
}

/// <summary>What a flow can use while it runs.</summary>
/// <param name="Existing">The integration being re-authorized or extended; <c>null</c> during setup.</param>
public sealed record FlowContext(TimeProvider Time, ILogger Logger, IntegrationInstance? Existing)
{
    /// <summary>
    /// The home's language (<c>pt-BR</c> or <c>en</c>), for what the flow suggests in it: the integration's name, or the
    /// names of the devices a virtual connector makes up. Texts shown to the user stay <see cref="LocalizedText"/>.
    /// </summary>
    public string Language { get; init; } = "en";
}

/// <summary>One step of a connector flow (JSON discriminator <c>type</c>).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FormStep), "form")]
[JsonDerivedType(typeof(ExternalUrlStep), "externalUrl")]
[JsonDerivedType(typeof(ProgressStep), "progress")]
[JsonDerivedType(typeof(ChooseStep), "choose")]
[JsonDerivedType(typeof(DoneStep), "done")]
[JsonDerivedType(typeof(AbortStep), "abort")]
public abstract record FlowStep
{
    private protected FlowStep()
    {
    }
}

/// <summary>Asks the user to fill in fields (rendered with the shared schema form).</summary>
/// <param name="StepId">Identifies the step in <see cref="IConnectorFlow.ContinueAsync"/>.</param>
public sealed record FormStep(string StepId, LocalizedText Title, LocalizedText? Description, IReadOnlyList<FieldSchema> Fields)
    : FlowStep
{
    /// <summary>Messages by field key after a rejected submission (e.g. "wrong password").</summary>
    public IReadOnlyDictionary<string, LocalizedText> Errors { get; init; } = new Dictionary<string, LocalizedText>();

    /// <summary>Initial values (a JSON object), or <c>null</c>. Never includes secrets.</summary>
    public JsonElement? Values { get; init; }
}

/// <summary>Sends the user to a vendor page (e.g. to create a token or authorize an app), optionally pasting a value back.</summary>
/// <param name="Url">An absolute <c>https</c> URL.</param>
/// <param name="PasteBack">The field the user pastes the result into, or <c>null</c> when none is needed.</param>
public sealed record ExternalUrlStep(
    string StepId,
    LocalizedText Title,
    LocalizedText? Description,
    Uri Url,
    FieldSchema? PasteBack) : FlowStep;

/// <summary>
/// Waits for something to happen on the device (press the pairing button, join the network). The client polls with an
/// empty input; the flow answers with the same step until done or <paramref name="Timeout"/> passes.
/// </summary>
public sealed record ProgressStep(string StepId, LocalizedText Title, LocalizedText? Description, TimeSpan? Timeout) : FlowStep;

/// <summary>Asks the user to pick one option; the answer is <c>{"choice":"&lt;key&gt;"}</c>.</summary>
public sealed record ChooseStep(string StepId, LocalizedText Title, LocalizedText? Description, IReadOnlyList<FlowChoice> Choices)
    : FlowStep;

/// <summary>One option of a <see cref="ChooseStep"/>.</summary>
public sealed record FlowChoice(string Key, LocalizedText Label, LocalizedText? Description);

/// <summary>The flow finished: create (or update) the integration with this configuration and these secrets.</summary>
/// <param name="Name">The suggested integration name.</param>
/// <param name="Config">The non-secret configuration (a JSON object), stored as <c>Integration.ConfigJson</c>.</param>
public sealed record DoneStep(string Name, JsonElement Config) : FlowStep
{
    /// <summary>The secrets to store encrypted. Never serialized (not part of any API response).</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
}

/// <summary>The flow cannot continue (unsupported device, already configured, timed out).</summary>
/// <param name="Reason">A stable snake_case code (e.g. <c>already_configured</c>, <c>timeout</c>).</param>
public sealed record AbortStep(string Reason, LocalizedText Message) : FlowStep;
