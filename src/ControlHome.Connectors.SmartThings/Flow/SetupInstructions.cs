using System.Text.Json;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Connectors.SmartThings.Flow;

/// <summary>
/// The wizard's instructions for creating the household's OAuth-In app — data (the embedded
/// <c>Resources/setup-instructions.json</c>, kept in step with <c>docs/connectors/smartthings.md</c>), because Samsung keeps
/// changing that path.
/// </summary>
internal sealed record SetupInstructions(
    Uri DocsUrl,
    string CliCommand,
    string AppType,
    IReadOnlyList<string> Scopes,
    string DefaultRedirectUri,
    LocalizedText Title,
    IReadOnlyList<string> StepsEn,
    IReadOnlyList<string> StepsPtBr)
{
    public const string ResourceName = "ControlHome.Connectors.SmartThings.setup-instructions.json";

    private static readonly Lazy<SetupInstructions> _default = new(() =>
    {
        using var stream = typeof(SetupInstructions).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    });

    public static SetupInstructions Default => _default.Value;

    /// <summary>The steps as one paragraph ("1) … 2) …") with the placeholders filled in.</summary>
    public LocalizedText Description => new(Join(StepsEn), Join(StepsPtBr));

    /// <exception cref="InvalidOperationException">The data is incomplete.</exception>
    public static SetupInstructions Parse(JsonElement root)
    {
        var docs = root.Str("docsUrl") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var docsUrl) && docsUrl.Scheme == Uri.UriSchemeHttps
            ? docsUrl
            : throw new InvalidOperationException("setup-instructions.json: docsUrl must be an https URL.");
        var title = root.Get("title");
        var steps = root.Get("steps");
        var en = Json.Strings(steps.Get("en"));
        var ptBr = Json.Strings(steps.Get("ptBr"));
        if (en.Count == 0 || en.Count != ptBr.Count)
        {
            throw new InvalidOperationException("setup-instructions.json: steps.en and steps.ptBr must list the same steps.");
        }

        return new SetupInstructions(
            docs,
            root.Str("cliCommand") ?? throw new InvalidOperationException("setup-instructions.json: cliCommand is missing."),
            root.Str("appType") ?? "OAuth-In App",
            Json.Strings(root.Get("scopes")) is { Count: > 0 } scopes ? scopes : throw new InvalidOperationException("setup-instructions.json: scopes are missing."),
            root.Str("defaultRedirectUri") ?? "https://httpbin.org/get",
            new LocalizedText(title.Str("en") ?? "Create your own SmartThings app", title.Str("ptBr") ?? "Crie o seu próprio app SmartThings"),
            en,
            ptBr);
    }

    private string Join(IReadOnlyList<string> steps) =>
        string.Join(' ', steps.Select((step, i) => $"{i + 1}) {Fill(step)}"));

    private string Fill(string step) => step
        .Replace("{cli}", CliCommand, StringComparison.Ordinal)
        .Replace("{appType}", AppType, StringComparison.Ordinal)
        .Replace("{scopes}", string.Join(' ', Scopes), StringComparison.Ordinal)
        .Replace("{redirect}", DefaultRedirectUri, StringComparison.Ordinal);
}
