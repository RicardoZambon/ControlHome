using ControlHome.Application.Security;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The logger handed to connector runtimes (<c>IConnectorContext.Logger</c>). Messages and their properties pass through
/// unchanged (the Serilog redactor masks secrets in property values), but an exception is replaced by a
/// <see cref="RedactedException"/>: the Serilog redactor cannot rewrite an event's exception, whose message, inner exceptions
/// and data reach the sinks as they are, and a vendor's error text is a natural place for a token
/// (<c>logger.LogError(ex, "Login failed")</c>).
/// </summary>
internal sealed class RedactingLogger(ILogger inner, KnownSecrets secrets) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        inner.Log(logLevel, eventId, state, exception is null ? null : RedactedException.Of(exception, secrets), formatter);
}

/// <summary>
/// An exception rendered as text with every known secret masked: <see cref="ToString"/> is the original's (type, message,
/// inner exceptions, stack trace), redacted; nothing else of the original (inner exception objects, <c>Data</c>) is kept.
/// </summary>
public sealed class RedactedException : Exception
{
    private readonly string? _text;

    public RedactedException()
    {
    }

    public RedactedException(string message)
        : base(message)
    {
    }

    public RedactedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private RedactedException(string message, string text)
        : base(message)
    {
        _text = text;
    }

    /// <summary><paramref name="exception"/> with every secret in <paramref name="secrets"/> masked.</summary>
    public static RedactedException Of(Exception exception, KnownSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(secrets);
        string message;
        string text;
        try
        {
            message = exception.Message;
            text = exception.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A misbehaving Message or ToString override: keep only the type.
            message = exception.GetType().FullName ?? exception.GetType().Name;
            text = message;
        }

        return new RedactedException(secrets.Redact(message), secrets.Redact(text));
    }

    public override string ToString() => _text ?? base.ToString();
}
