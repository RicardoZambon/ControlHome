using System.Globalization;

namespace ControlHome.Connectors.DahuaHttp.Model;

/// <summary>
/// The recorder channels to import, as typed in the setup form: numbers and ranges separated by commas (<c>1-4, 6</c>).
/// </summary>
internal sealed class ChannelSelection
{
    /// <summary>The highest channel number accepted (recorders have at most a few hundred).</summary>
    public const int MaxChannel = 256;

    private readonly SortedSet<int> _channels;

    private ChannelSelection(SortedSet<int> channels) => _channels = channels;

    public IReadOnlyCollection<int> Channels => _channels;

    public bool Contains(int channel) => _channels.Contains(channel);

    /// <summary>Parses a selection; <c>null</c> for empty text (every channel).</summary>
    /// <exception cref="FormatException">Not numbers and ranges between 1 and <see cref="MaxChannel"/>.</exception>
    public static ChannelSelection? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var channels = new SortedSet<int>();
        foreach (var item in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = item.IndexOf('-', StringComparison.Ordinal);
            var (from, to) = dash < 0
                ? (Number(item), Number(item))
                : (Number(item[..dash]), Number(item[(dash + 1)..]));
            if (from > to)
            {
                throw new FormatException($"The channel range '{item}' is reversed.");
            }

            for (var channel = from; channel <= to; channel++)
            {
                channels.Add(channel);
            }
        }

        return channels.Count == 0 ? null : new ChannelSelection(channels);
    }

    public override string ToString() => string.Join(",", _channels);

    private static int Number(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > MaxChannel)
        {
            throw new FormatException($"'{text}' is not a channel number between 1 and {MaxChannel}.");
        }

        return number;
    }
}
