using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Application.Security;
using Serilog.Core;
using Serilog.Events;

namespace ControlHome.Api.Hosting;

/// <summary>
/// Masks every secret this process knows (<see cref="KnownSecrets"/>: integration tokens, passwords, per-device keys) in
/// every Serilog event: as an enricher it rewrites property values (scalars, and inside sequences, structures and
/// dictionaries) after they were captured; as a destructuring policy it renders JSON values as redacted text (without the
/// HTML-safe escaping, so a secret containing <c>+</c>, <c>'</c>, <c>&lt;</c>, <c>&gt;</c> or <c>&amp;</c> is still found).
/// Message templates are constant text. An event's exception cannot be rewritten here: the runtime logs connector failures
/// as redacted messages, and connectors get a logger that redacts the exceptions they log (Infrastructure's
/// <c>RedactingLogger</c>). Register it as the last enricher.
/// </summary>
public sealed class KnownSecretsRedactor(KnownSecrets secrets) : ILogEventEnricher, IDestructuringPolicy
{
    /// <summary>Only the escaping JSON itself needs: secrets appear in the text as they were registered.</summary>
    private static readonly JsonSerializerOptions _unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (secrets.IsEmpty)
        {
            return;
        }

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        switch (value)
        {
            case JsonElement element:
                // Re-serialized, not GetRawText(): the raw text keeps whatever escapes its source used (e.g. +).
                result = new ScalarValue(secrets.Redact(Render(element)));
                return true;
            case JsonNode node:
                result = new ScalarValue(secrets.Redact(node.ToJsonString(_unescaped)));
                return true;
            default:
                result = null;
                return false;
        }
    }

    private static string Render(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _unescaped.Encoder }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The value with secrets masked; the same instance when it holds none.</summary>
    private LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                return secrets.ContainsSecret(text) ? new ScalarValue(secrets.Redact(text)) : value;
            case ScalarValue { Value: not null and not string } scalar when scalar.Value.ToString() is { } text && secrets.ContainsSecret(text):
                return new ScalarValue(secrets.Redact(text));
            case SequenceValue sequence:
            {
                var elements = sequence.Elements.Select(Redact).ToList();
                return elements.Where((e, i) => !ReferenceEquals(e, sequence.Elements[i])).Any() ? new SequenceValue(elements) : value;
            }

            case StructureValue structure:
            {
                var changed = false;
                var properties = structure.Properties.Select(p =>
                {
                    var redacted = Redact(p.Value);
                    changed |= !ReferenceEquals(redacted, p.Value);
                    return new LogEventProperty(p.Name, redacted);
                }).ToList();
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var elements = dictionary.Elements.Select(e =>
                {
                    var key = (ScalarValue)Redact(e.Key);
                    var redacted = Redact(e.Value);
                    changed |= !ReferenceEquals(redacted, e.Value) || !ReferenceEquals(key, e.Key);
                    return new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, redacted);
                }).ToList();
                return changed ? new DictionaryValue(elements) : value;
            }

            default:
                return value;
        }
    }
}
