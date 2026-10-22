using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Api.Hosting;
using ControlHome.Api.IntegrationTests.Devices;
using ControlHome.Application.Security;
using Serilog;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// The Serilog redactor (wired as in <c>LoggingSetup</c>) with a secret made of characters JSON escapes by default
/// (<c>+ ' &lt; &gt; &amp;</c>), as vendor keys and base64 tokens often are.
/// </summary>
public sealed class KnownSecretsRedactorTests
{
    private const string Secret = "a1'b2+c3<d4>&e5";

    [Fact]
    public void Json_values_are_matched_unescaped()
    {
        var (logger, capture) = Logger();
        var node = new JsonObject { ["local_key"] = Secret, ["host"] = "10.0.0.7" };
        using var document = JsonDocument.Parse("""{"local_key":"a1'b2+c3<d4>&e5"}""");

        logger.Information("Node {@Settings}", node);
        logger.Information("Element {@Settings}", document.RootElement);

        // Escaped JSON (a1'b2+…) would not contain the secret either: the mask proves it was found.
        capture.Events.Count.ShouldBe(2);
        capture.Events.ShouldAllBe(e => !e.Contains(Secret) && !e.Contains("\\u002B") && e.Contains("local_key") && e.Contains("***"));
        capture.Events[0].ShouldContain("10.0.0.7");
    }

    [Fact]
    public void Scalar_and_structured_properties_are_masked()
    {
        var (logger, capture) = Logger();

        logger.Information("Token {Token} in {@Settings}", Secret, new { Url = $"https://vendor.example/?key={Secret}" });

        var text = capture.Events.ShouldHaveSingleItem();
        text.ShouldNotContain(Secret);
        text.ShouldContain("key=***");
    }

    private static (ILogger Logger, LogCapture Capture) Logger()
    {
        var secrets = new KnownSecrets();
        secrets.Register(Secret);
        var redactor = new KnownSecretsRedactor(secrets);
        var capture = new LogCapture();
        var logger = new LoggerConfiguration()
            .Destructure.With(redactor)
            .Enrich.With(redactor)
            .WriteTo.Sink(capture)
            .CreateLogger();
        return (logger, capture);
    }
}
