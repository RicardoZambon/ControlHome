namespace ControlHome.Application.Security;

/// <summary>
/// Every secret value this process has seen (integration tokens and passwords, per-device keys), so log events, status
/// messages and error texts can be scrubbed before they leave the process (<see cref="Redact"/>; the Serilog
/// <c>KnownSecretsRedactor</c> applies it to every log event). Values shorter than <see cref="MinLength"/> are not
/// tracked (masking them would mangle unrelated text). Thread-safe; values are never removed while the process runs.
/// </summary>
public sealed class KnownSecrets
{
    public const int MinLength = 4;

    /// <summary>What a secret is replaced with.</summary>
    public const string Mask = "***";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    /// <summary>The values, longest first (so a secret containing another one is masked whole).</summary>
    private volatile string[] _snapshot = [];

    public bool IsEmpty => _snapshot.Length == 0;

    /// <summary>Starts masking <paramref name="value"/> (no-op for <c>null</c> and short values).</summary>
    public void Register(string? value)
    {
        if (value is null || value.Length < MinLength)
        {
            return;
        }

        lock (_lock)
        {
            if (_values.Add(value))
            {
                _snapshot = [.. _values.OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> contains a known secret.</summary>
    public bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var secret in _snapshot)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="text"/> with every known secret replaced by <see cref="Mask"/>.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = text;
        foreach (var secret in _snapshot)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary><see cref="Redact(string)"/> that passes <c>null</c> through.</summary>
    public string? RedactOrNull(string? text) => text is null ? null : Redact(text);
}
