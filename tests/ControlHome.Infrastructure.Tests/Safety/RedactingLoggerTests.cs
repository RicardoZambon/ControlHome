using ControlHome.Application.Security;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Tests.Safety;

/// <summary>The logger connectors get: an exception they log reaches the sinks only as redacted text.</summary>
public sealed class RedactingLoggerTests
{
    private const string Secret = "tok+en'<&>-0042";

    [Fact]
    public void A_logged_exception_is_replaced_by_its_redacted_text()
    {
        var secrets = new KnownSecrets();
        secrets.Register(Secret);
        var inner = new RecordingLogger();
        var logger = new RedactingLogger(inner, secrets);
        var original = Thrown(new InvalidOperationException($"Vendor rejected {Secret}", new HttpRequestException($"401 for {Secret}")));

        logger.LogError(original, "Login failed for {User}", "kitchen");

        var (level, message, exception) = inner.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Error);
        message.ShouldBe("Login failed for kitchen");
        var redacted = exception.ShouldBeOfType<RedactedException>();
        redacted.InnerException.ShouldBeNull();
        redacted.Message.ShouldBe("Vendor rejected ***");
        var text = redacted.ToString();
        text.ShouldNotContain(Secret);
        text.ShouldContain("System.InvalidOperationException: Vendor rejected ***");
        text.ShouldContain("System.Net.Http.HttpRequestException: 401 for ***");
        text.ShouldContain(nameof(Thrown)); // the stack trace is kept
    }

    [Fact]
    public void Messages_without_an_exception_pass_through()
    {
        var inner = new RecordingLogger();
        var logger = new RedactingLogger(inner, new KnownSecrets());

        logger.LogInformation("Connected");

        inner.Entries.ShouldHaveSingleItem().Exception.ShouldBeNull();
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
