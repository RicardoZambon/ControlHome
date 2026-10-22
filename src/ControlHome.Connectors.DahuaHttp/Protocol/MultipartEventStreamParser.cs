using System.Globalization;
using System.Text;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>
/// An incremental parser for the <c>multipart/x-mixed-replace</c> body of <c>eventManager.cgi?action=attach</c>: bytes go
/// in as they arrive (<see cref="Feed"/>), the text of each complete part comes out (trimmed; empty parts are skipped).
/// <para>
/// Firmware differs, so it is tolerant: CRLF or LF line ends; the delimiter <c>--&lt;boundary&gt;</c> (when the declared
/// boundary already starts with <c>--</c>, also the boundary itself — the first of the two that shows up is used; when
/// none was declared, the first line starting with <c>--</c>); part headers optional; a part with <c>Content-Length</c>
/// is complete after that many bytes — without waiting for the next delimiter, which may come only with the next
/// heartbeat — plus the rest of its last line, since some firmware counts short (a byte or two, or one per line when it
/// counts LF but sends CRLF), and plus further lines while a <c>data={…}</c> object it opened is not closed yet. A part
/// without a length ends at the next delimiter; a length that runs into the next delimiter is cut there. Preamble,
/// garbage between parts and parts larger than <see cref="MaxPartBytes"/> are skipped.
/// </para>
/// </summary>
internal sealed class MultipartEventStreamParser
{
    /// <summary>The largest part kept (event texts are a few hundred bytes; access events with data a few KB).</summary>
    public const int DefaultMaxPartBytes = 64 * 1024;

    private readonly List<byte> _buffer = [];
    private readonly List<string> _parts = [];
    private readonly StringBuilder _body = new();
    private readonly string[] _declared;
    private byte[]? _delimiter;
    private State _state = State.SeekBoundary;
    private int _remaining;
    private bool _skipping;

    // JSON nesting of the part's text so far (outside strings): a counted part is not cut inside its data object.
    private int _depth;
    private bool _inString;
    private bool _escaped;

    public MultipartEventStreamParser(string? boundary, int maxPartBytes = DefaultMaxPartBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPartBytes, 64);
        MaxPartBytes = maxPartBytes;
        var trimmed = boundary?.Trim().Trim('"');
        _declared = string.IsNullOrEmpty(trimmed) ? []
            : trimmed.StartsWith("--", StringComparison.Ordinal) ? [trimmed, "--" + trimmed]
            : ["--" + trimmed];
    }

    private enum State
    {
        SeekBoundary,
        Headers,
        BodyDelimited,
        BodyCounted,
        BodyCountedTail,
    }

    public int MaxPartBytes { get; }

    /// <summary>The delimiter line in use (<c>--myboundary</c>), once known.</summary>
    public string? Delimiter => _delimiter is null ? null : Encoding.ASCII.GetString(_delimiter);

    /// <summary>Adds received bytes; returns the parts they completed, in order.</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> data)
    {
        _parts.Clear();
        foreach (var b in data)
        {
            _buffer.Add(b);
        }

        Process();
        return [.. _parts];
    }

    /// <summary>The stream ended: returns a last part that was waiting for its delimiter, if any.</summary>
    public IReadOnlyList<string> Complete()
    {
        _parts.Clear();
        if (_state is State.BodyDelimited or State.BodyCounted or State.BodyCountedTail)
        {
            if (_buffer.Count > 0 && !_skipping)
            {
                var cut = FindDelimiterAtLineStart(_buffer.Count);
                Append(Decode(_buffer, 0, cut >= 0 ? cut : _buffer.Count));
            }

            Emit();
        }

        _buffer.Clear();
        _state = State.SeekBoundary;
        return [.. _parts];
    }

    private void Process()
    {
        while (true)
        {
            switch (_state)
            {
                case State.BodyCounted:
                    if (!TakeCounted())
                    {
                        return;
                    }

                    break;
                default:
                    if (!TakeLine(out var line))
                    {
                        if (_buffer.Count > MaxPartBytes)
                        {
                            // A line longer than any part: drop it and look for the next delimiter.
                            _buffer.Clear();
                            ResetPart();
                            _state = State.SeekBoundary;
                        }

                        return;
                    }

                    OnLine(line);
                    break;
            }
        }
    }

    private void OnLine(string line)
    {
        var isDelimiter = IsDelimiter(line);
        switch (_state)
        {
            case State.SeekBoundary:
                if (isDelimiter)
                {
                    StartPart();
                }

                break;

            case State.Headers:
                if (isDelimiter)
                {
                    StartPart();
                }
                else if (line.Length == 0)
                {
                    _state = _remaining > 0 ? State.BodyCounted : State.BodyDelimited;
                }
                else if (TryParseHeader(line, out var name, out var value))
                {
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    {
                        _remaining = length;
                    }
                }
                else
                {
                    // No part headers: the line is already the body.
                    _state = State.BodyDelimited;
                    AppendLine(line);
                }

                break;

            case State.BodyDelimited:
                if (isDelimiter)
                {
                    Emit();
                    StartPart();
                }
                else
                {
                    AppendLine(line);
                }

                break;

            case State.BodyCountedTail:
                if (isDelimiter)
                {
                    Emit();
                    StartPart();
                }
                else
                {
                    AppendLine(line);
                    if (Closed)
                    {
                        Emit();
                        _state = State.SeekBoundary;
                    }
                }

                break;
        }
    }

    /// <summary>Takes the counted body once enough bytes arrived (cut at a delimiter that shows up inside it).</summary>
    private bool TakeCounted()
    {
        if (_remaining > MaxPartBytes)
        {
            _skipping = true;
            _remaining = Math.Min(_remaining, MaxPartBytes);
        }

        // A delimiter inside the counted bytes means the length is wrong: the part ends there (even before all of them came).
        var count = _remaining;
        var cut = FindDelimiterAtLineStart(Math.Min(count, _buffer.Count));
        if (cut < 0 && _buffer.Count < count)
        {
            return false;
        }

        if (cut >= 0)
        {
            if (!_skipping)
            {
                Append(Decode(_buffer, 0, cut));
            }

            _buffer.RemoveRange(0, cut);
            Emit();
            _state = State.SeekBoundary;
            return true;
        }

        if (!_skipping)
        {
            Append(Decode(_buffer, 0, count));
        }

        var endsLine = count == 0 || _buffer[count - 1] == (byte)'\n';
        _buffer.RemoveRange(0, count);
        if (endsLine && Closed)
        {
            Emit();
            _state = State.SeekBoundary;
        }
        else
        {
            // The rest of the last line, and more lines while the data object is open (the count was short).
            _state = State.BodyCountedTail;
        }

        return true;
    }

    /// <summary>The part's text so far closes every object and array it opened (a skipped part counts as closed).</summary>
    private bool Closed => _skipping || _depth <= 0;

    /// <summary>The offset (≤ <paramref name="limit"/>) of a delimiter that starts a line inside the first bytes, or -1.</summary>
    private int FindDelimiterAtLineStart(int limit)
    {
        if (_delimiter is null)
        {
            return -1;
        }

        for (var i = 0; i < limit; i++)
        {
            if ((i == 0 || _buffer[i - 1] == (byte)'\n') && StartsWithDelimiter(i))
            {
                return i;
            }
        }

        return -1;
    }

    private bool StartsWithDelimiter(int offset)
    {
        if (offset + _delimiter!.Length > _buffer.Count)
        {
            return false;
        }

        for (var j = 0; j < _delimiter.Length; j++)
        {
            if (_buffer[offset + j] != _delimiter[j])
            {
                return false;
            }
        }

        return true;
    }

    private bool TakeLine(out string line)
    {
        var newline = _buffer.IndexOf((byte)'\n');
        if (newline < 0)
        {
            line = string.Empty;
            return false;
        }

        var end = newline > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
        line = Decode(_buffer, 0, end);
        _buffer.RemoveRange(0, newline + 1);
        return true;
    }

    private bool IsDelimiter(string line)
    {
        var trimmed = line.TrimEnd();
        if (_delimiter is null)
        {
            if (_state != State.SeekBoundary || trimmed.Length <= 2 || !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            if (_declared.Length > 0)
            {
                // Declared: the first of its spellings that shows up is the one this device uses.
                var match = _declared.FirstOrDefault(d => string.Equals(trimmed, d, StringComparison.Ordinal)
                    || string.Equals(trimmed, d + "--", StringComparison.Ordinal));
                if (match is null)
                {
                    return false;
                }

                _delimiter = Encoding.ASCII.GetBytes(match);
                return true;
            }

            // Not declared: the first line that looks like one defines it.
            _delimiter = Encoding.ASCII.GetBytes(trimmed.EndsWith("--", StringComparison.Ordinal) && trimmed.Length > 4 ? trimmed[..^2] : trimmed);
            return true;
        }

        var delimiter = Delimiter!;
        return string.Equals(trimmed, delimiter, StringComparison.Ordinal)
            || string.Equals(trimmed, delimiter + "--", StringComparison.Ordinal);
    }

    private static bool TryParseHeader(string line, out string name, out string value)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        name = colon > 0 ? line[..colon].Trim() : string.Empty;
        value = colon > 0 ? line[(colon + 1)..].Trim() : string.Empty;
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private void StartPart()
    {
        ResetPart();
        _state = State.Headers;
    }

    private void ResetPart()
    {
        _body.Clear();
        _remaining = 0;
        _skipping = false;
        _depth = 0;
        _inString = false;
        _escaped = false;
    }

    private void AppendLine(string line)
    {
        if (_skipping)
        {
            return;
        }

        if (_body.Length + line.Length + 1 > MaxPartBytes)
        {
            _skipping = true;
            _body.Clear();
            return;
        }

        Append(line);
        Append("\n");
    }

    /// <summary>Adds text to the part, following its JSON nesting (braces and brackets outside strings).</summary>
    private void Append(string text)
    {
        _body.Append(text);
        foreach (var c in text)
        {
            if (_inString)
            {
                if (_escaped)
                {
                    _escaped = false;
                }
                else if (c == '\\')
                {
                    _escaped = true;
                }
                else if (c == '"')
                {
                    _inString = false;
                }
            }
            else if (c == '"')
            {
                _inString = true;
            }
            else if (c is '{' or '[')
            {
                _depth++;
            }
            else if (c is '}' or ']')
            {
                _depth--;
            }
        }
    }

    private void Emit()
    {
        if (!_skipping)
        {
            var text = _body.ToString().Trim();
            if (text.Length > 0)
            {
                _parts.Add(text);
            }
        }

        ResetPart();
    }

    private static string Decode(List<byte> buffer, int start, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[count];
        buffer.CopyTo(start, bytes, 0, count);
        return Encoding.UTF8.GetString(bytes);
    }
}
