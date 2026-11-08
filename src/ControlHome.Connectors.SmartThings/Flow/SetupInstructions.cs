using System.Text.Json;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Connectors.SmartThings.Flow;

/// <summary>
/// The wizard's instructions for the household's OAuth-In app — data (the embedded
/// <c>Resources/setup-instructions.json</c>, kept in step with <c>docs/connectors/smartthings.md</c>), because Samsung keeps
/// changing that path. Two ways: ControlHome creates the app with a short-lived token (<see cref="Create"/>), or the
/// household creates it with the SmartThings CLI and types in its client id and secret.
/// </summary>
internal sealed record SetupInstructions(
    Uri DocsUrl,
    string CliCommand,
    string AppType,
    IReadOnlyList<string> Scopes,
    string DefaultRedirectUri,
    LocalizedText Title,
    IReadOnlyList<string> StepsEn,
    IReadOnlyList<string> StepsPtBr,
    AppCreation Create)
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

    /// <summary>The CLI steps as a numbered list, one per line ("1. …"), with the placeholders filled in.</summary>
    public LocalizedText Description => new(Join(StepsEn), Join(StepsPtBr));

    /// <summary>The steps for letting ControlHome create the app, as a numbered list.</summary>
    public LocalizedText CreateDescription => new(Join(Create.StepsEn), Join(Create.StepsPtBr));

    /// <exception cref="InvalidOperationException">The data is incomplete.</exception>
    public static SetupInstructions Parse(JsonElement root)
    {
        var docs = HttpsUrl(root.Str("docsUrl"), "docsUrl");
        var title = root.Get("title");
        var (en, ptBr) = Steps(root.Get("steps"), "steps");
        var create = root.Get("create");
        var createTitle = create.Get("title");
        var (createEn, createPtBr) = Steps(create.Get("steps"), "create.steps");

        return new SetupInstructions(
            docs,
            root.Str("cliCommand") ?? throw new InvalidOperationException("setup-instructions.json: cliCommand is missing."),
            root.Str("appType") ?? "API Access",
            Json.Strings(root.Get("scopes")) is { Count: > 0 } scopes ? scopes : throw new InvalidOperationException("setup-instructions.json: scopes are missing."),
            root.Str("defaultRedirectUri") ?? "https://httpbin.org/get",
            new LocalizedText(title.Str("en") ?? "Create your own SmartThings app", title.Str("ptBr") ?? "Crie o seu próprio app SmartThings"),
            en,
            ptBr,
            new AppCreation(
                HttpsUrl(create.Str("tokensUrl"), "create.tokensUrl"),
                create.Str("appDisplayName") ?? "ControlHome",
                create.Str("appDescription") ?? "Self-hosted home control",
                new LocalizedText(
                    createTitle.Str("en") ?? "Let ControlHome create your app",
                    createTitle.Str("ptBr") ?? "Deixe o ControlHome criar o seu app"),
                createEn,
                createPtBr));
    }

    private static Uri HttpsUrl(string? value, string name) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : throw new InvalidOperationException($"setup-instructions.json: {name} must be an https URL.");

    private static (IReadOnlyList<string> En, IReadOnlyList<string> PtBr) Steps(JsonElement? steps, string name)
    {
        var en = Json.Strings(steps.Get("en"));
        var ptBr = Json.Strings(steps.Get("ptBr"));
        return en.Count == 0 || en.Count != ptBr.Count
            ? throw new InvalidOperationException($"setup-instructions.json: {name}.en and {name}.ptBr must list the same steps.")
            : (en, ptBr);
    }

    private string Join(IReadOnlyList<string> steps) =>
        string.Join('\n', steps.Select((step, i) => $"{i + 1}. {Fill(step)}"));

    private string Fill(string step) => step
        .Replace("{cli}", CliCommand, StringComparison.Ordinal)
        .Replace("{appType}", AppType, StringComparison.Ordinal)
        .Replace("{scopes}", string.Join(' ', Scopes), StringComparison.Ordinal)
        .Replace("{redirect}", DefaultRedirectUri, StringComparison.Ordinal);
}

/// <summary>Letting ControlHome create the app with a short-lived personal access token: the tokens page and the app it makes.</summary>
internal sealed record AppCreation(
    Uri TokensUrl,
    string AppDisplayName,
    string AppDescription,
    LocalizedText Title,
    IReadOnlyList<string> StepsEn,
    IReadOnlyList<string> StepsPtBr);
