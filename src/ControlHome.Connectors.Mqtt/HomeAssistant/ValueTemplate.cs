using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlHome.Connectors.Mqtt.Model;

namespace ControlHome.Connectors.Mqtt.HomeAssistant;

/// <summary>The value a template produced: text (as Jinja would render it) and, when it came from JSON, the JSON value.</summary>
internal sealed record TemplateValue(string Text, JsonElement? Json)
{
    public double? Number => Json is { ValueKind: JsonValueKind.Number } json ? json.GetDouble() : Conversions.Number(Text);
}

/// <summary>
/// The subset of Home Assistant's Jinja value templates ControlHome evaluates: exactly one <c>{{ … }}</c> expression whose
/// operand is <c>value</c> or <c>value_json</c> followed by <c>.key</c>, <c>["key"]</c>, <c>['key']</c> or <c>[0]</c>
/// accessors, piped through any of <c>float</c>, <c>float(default)</c>, <c>int</c>, <c>int(default)</c>, <c>round</c>,
/// <c>round(n)</c>, <c>string</c>, <c>lower</c>, <c>upper</c>, <c>trim</c>. Anything else (literal text around the
/// expression, conditionals, arithmetic, other filters) is unsupported: <see cref="TryParse"/> returns <c>false</c> and the
/// attribute is marked unsupported instead of failing.
/// </summary>
internal sealed class ValueTemplate
{
    /// <summary>The template that returns the payload unchanged (no template configured).</summary>
    public static ValueTemplate Identity { get; } = new(null, [], []);

    private readonly string? _root;
    private readonly IReadOnlyList<Accessor> _path;
    private readonly IReadOnlyList<Filter> _filters;

    private ValueTemplate(string? root, IReadOnlyList<Accessor> path, IReadOnlyList<Filter> filters)
    {
        _root = root;
        _path = path;
        _filters = filters;
    }

    /// <summary>Parses a template; <c>null</c>/blank means <see cref="Identity"/>.</summary>
    public static bool TryParse(string? template, out ValueTemplate result)
    {
        result = Identity;
        if (string.IsNullOrWhiteSpace(template))
        {
            return true;
        }

        var parser = new Parser(template.Trim());
        if (parser.TryParseTemplate() is not { } parsed)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    /// <summary>
    /// Evaluates the template on a payload; <c>null</c> when the value is missing (unknown key, failed conversion without
    /// default) — the attribute keeps its previous value.
    /// </summary>
    public TemplateValue? Evaluate(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        TemplateValue? current;
        if (_root is null or "value")
        {
            current = new TemplateValue(payload, null);
        }
        else
        {
            if (Model.Json.TryParse(payload) is not { } json)
            {
                return null;
            }

            var element = json;
            foreach (var accessor in _path)
            {
                if (accessor.Apply(element) is not { } next)
                {
                    return null;
                }

                element = next;
            }

            current = element.ValueKind == JsonValueKind.Null ? null : new TemplateValue(Render(element), element);
        }

        foreach (var filter in _filters)
        {
            if (current is null)
            {
                return null;
            }

            current = filter.Apply(current);
        }

        return current;
    }

    /// <summary>How Jinja renders a JSON value: strings as is, numbers invariant, booleans <c>True</c>/<c>False</c>.</summary>
    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };

    private sealed record Accessor(string? Key, int? Index)
    {
        public JsonElement? Apply(JsonElement element) =>
            Key is not null
                ? element.ValueKind == JsonValueKind.Object && element.TryGetProperty(Key, out var member) ? member : null
                : element.ValueKind == JsonValueKind.Array && Index is { } index && index >= 0 && index < element.GetArrayLength() ? element[index] : null;
    }

    private sealed record Filter(string Name, double? Argument)
    {
        public TemplateValue? Apply(TemplateValue value)
        {
            switch (Name)
            {
                case "float":
                    return Number(value) is { } f ? Of(f) : Argument is { } fallback ? Of(fallback) : null;
                case "int":
                    var number = Number(value);
                    return number is { } i ? Of(Math.Truncate(i)) : Argument is { } intFallback ? Of(Math.Truncate(intFallback)) : null;
                case "round":
                    return Number(value) is { } r ? Of(Math.Round(r, (int)(Argument ?? 0), MidpointRounding.ToEven)) : null;
                case "string":
                    return new TemplateValue(value.Text, null);
                case "lower":
                    return new TemplateValue(value.Text.ToLowerInvariant(), null);
                case "upper":
                    return new TemplateValue(value.Text.ToUpperInvariant(), null);
                case "trim":
                    return new TemplateValue(value.Text.Trim(), null);
                default:
                    return null;
            }
        }

        private static double? Number(TemplateValue value) =>
            value.Json is { ValueKind: JsonValueKind.True or JsonValueKind.False } flag ? (flag.GetBoolean() ? 1 : 0) : value.Number;

        private static TemplateValue Of(double number)
        {
            var text = number.ToString("R", CultureInfo.InvariantCulture);
            using var document = JsonDocument.Parse(text);
            return new TemplateValue(text, document.RootElement.Clone());
        }
    }

    /// <summary>A tiny recursive-descent parser for the supported subset.</summary>
    private sealed class Parser(string text)
    {
        private static readonly HashSet<string> _filters = new(StringComparer.Ordinal) { "float", "int", "round", "string", "lower", "upper", "trim" };
        private int _position;

        public ValueTemplate? TryParseTemplate()
        {
            if (!Literal("{{"))
            {
                return null;
            }

            SkipSpace();
            var root = Identifier();
            if (root is not ("value" or "value_json"))
            {
                return null;
            }

            var path = new List<Accessor>();
            while (true)
            {
                SkipSpace();
                if (Literal("."))
                {
                    SkipSpace();
                    if (Identifier() is not { } key)
                    {
                        return null;
                    }

                    path.Add(new Accessor(key, null));
                }
                else if (Literal("["))
                {
                    SkipSpace();
                    if (Quoted() is { } quoted)
                    {
                        path.Add(new Accessor(quoted, null));
                    }
                    else if (Integer() is { } index)
                    {
                        path.Add(new Accessor(null, index));
                    }
                    else
                    {
                        return null;
                    }

                    SkipSpace();
                    if (!Literal("]"))
                    {
                        return null;
                    }
                }
                else
                {
                    break;
                }
            }

            if (root == "value" && path.Count > 0)
            {
                return null;
            }

            var filters = new List<Filter>();
            while (Literal("|"))
            {
                SkipSpace();
                if (Identifier() is not { } name || !_filters.Contains(name))
                {
                    return null;
                }

                double? argument = null;
                SkipSpace();
                if (Literal("("))
                {
                    SkipSpace();
                    if (Number() is not { } parsed || name is not ("float" or "int" or "round"))
                    {
                        return null;
                    }

                    argument = parsed;
                    SkipSpace();
                    if (!Literal(")"))
                    {
                        return null;
                    }

                    SkipSpace();
                }

                if (name == "round" && argument is { } digits && (digits < 0 || digits > 10 || Math.Abs(digits - Math.Round(digits)) > 0))
                {
                    return null;
                }

                filters.Add(new Filter(name, argument));
            }

            SkipSpace();
            return Literal("}}") && _position == text.Length ? new ValueTemplate(root, path, filters) : null;
        }

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private bool Literal(string literal)
        {
            if (string.CompareOrdinal(text, _position, literal, 0, literal.Length) == 0)
            {
                _position += literal.Length;
                return true;
            }

            return false;
        }

        private string? Identifier()
        {
            var start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
            {
                _position++;
            }

            return _position > start && !char.IsAsciiDigit(text[start]) ? text[start.._position] : null;
        }

        private string? Quoted()
        {
            if (_position >= text.Length || text[_position] is not ('"' or '\''))
            {
                return null;
            }

            var quote = text[_position++];
            var builder = new StringBuilder();
            while (_position < text.Length && text[_position] != quote)
            {
                if (text[_position] == '\\')
                {
                    return null;
                }

                builder.Append(text[_position++]);
            }

            if (_position >= text.Length)
            {
                return null;
            }

            _position++;
            return builder.ToString();
        }

        private int? Integer()
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            {
                _position++;
            }

            return _position > start && int.TryParse(text.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private double? Number()
        {
            var start = _position;
            if (_position < text.Length && text[_position] == '-')
            {
                _position++;
            }

            while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
            {
                _position++;
            }

            return Conversions.Number(text[start.._position]);
        }
    }
}

/// <summary>
/// Home Assistant command templates, in the subset ControlHome renders: literal text with <c>{{ value }}</c> placeholders
/// (optionally piped through <c>int</c>, <c>float</c> or <c>round(n)</c>). Anything else is unsupported (the command is
/// rejected as unsupported instead of sending a wrong payload).
/// </summary>
internal sealed class CommandTemplate
{
    private readonly IReadOnlyList<(string Literal, ValueTemplate? Expression)> _parts;

    private CommandTemplate(IReadOnlyList<(string, ValueTemplate?)> parts) => _parts = parts;

    /// <summary>The template that sends the value as is.</summary>
    public static CommandTemplate Identity { get; } = new([(string.Empty, ValueTemplate.Identity)]);

    /// <param name="variable">
    /// The template's variable: <c>value</c> for most command templates, <c>position</c> for a cover's
    /// <c>set_position_template</c>.
    /// </param>
    public static bool TryParse(string? template, out CommandTemplate result, string variable = "value")
    {
        result = Identity;
        if (!string.IsNullOrEmpty(template) && variable != "value")
        {
            template = Regex.Replace(
                template,
                @"\{\{\s*" + Regex.Escape(variable) + @"(?![A-Za-z0-9_])",
                "{{ value",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        if (string.IsNullOrEmpty(template))
        {
            return true;
        }

        var parts = new List<(string, ValueTemplate?)>();
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                // Statements and comments ({% … %}, {# … #}) are not supported; "}}" alone is just JSON closing braces.
                if (template.IndexOf("{%", position, StringComparison.Ordinal) >= 0 || template.IndexOf("{#", position, StringComparison.Ordinal) >= 0)
                {
                    return false;
                }

                parts.Add((template[position..], null));
                break;
            }

            var close = template.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0
                || template.IndexOf("{%", position, open - position, StringComparison.Ordinal) >= 0
                || template.IndexOf("{#", position, open - position, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            var expression = template[open..(close + 2)];
            if (!ValueTemplate.TryParse(expression, out var parsed) || !expression.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith("{{value", StringComparison.Ordinal)
                || expression.Contains("value_json", StringComparison.Ordinal))
            {
                return false;
            }

            parts.Add((template[position..open], parsed));
            position = close + 2;
        }

        result = new CommandTemplate(parts);
        return true;
    }

    /// <summary>Renders the template with <c>value</c> = <paramref name="value"/> (already formatted as text).</summary>
    public string? Render(string value)
    {
        var builder = new StringBuilder();
        foreach (var (literal, expression) in _parts)
        {
            builder.Append(literal);
            if (expression is not null)
            {
                if (expression.Evaluate(value) is not { } rendered)
                {
                    return null;
                }

                builder.Append(rendered.Text);
            }
        }

        return builder.ToString();
    }
}
