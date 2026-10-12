using System.Security.Cryptography;
using System.Text;

namespace ControlHome.Application.Setup;

/// <summary>
/// The first-run setup code: 12 Crockford base32 characters from <see cref="RandomNumberGenerator"/>, shown as
/// <c>XXXX-XXXX-XXXX</c>. Candidates are compared in constant time after normalization (upper case, dashes and white space
/// removed).
/// </summary>
public static class SetupCode
{
    /// <summary>Crockford base32 (no I, L, O, U).</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 12;

    private const int GroupLength = 4;

    /// <summary>A new random code (normalized form, no dashes).</summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>Upper case, without dashes and white space; empty for null.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c != '-' && !char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>A generated (12-character) code as <c>XXXX-XXXX-XXXX</c>; anything else unchanged.</summary>
    public static string Format(string normalizedCode)
    {
        ArgumentNullException.ThrowIfNull(normalizedCode);
        if (normalizedCode.Length != Length)
        {
            return normalizedCode;
        }

        return string.Join('-', normalizedCode.Chunk(GroupLength).Select(group => new string(group)));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> normalizes to <paramref name="normalizedCode"/>. Both sides are hashed first, so
    /// the comparison takes the same time whatever the candidate's length or content.
    /// </summary>
    public static bool Matches(string normalizedCode, string? candidate)
    {
        ArgumentNullException.ThrowIfNull(normalizedCode);
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCode.Length == 0 || normalizedCandidate.Length == 0)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode), expected);
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCandidate), actual);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
