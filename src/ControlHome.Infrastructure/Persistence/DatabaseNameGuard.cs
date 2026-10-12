using System.Text.RegularExpressions;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// The single gate for every <c>CREATE DATABASE</c> / <c>DROP DATABASE</c> done by tools, scripts and tests. The SQL server
/// also hosts other databases: only <c>ControlHome_(Dev|Test|Compose)[_suffix]</c> names (case-sensitive) are allowed.
/// The running app does not use it: it only ever creates the database the operator configured (and never drops one).
/// </summary>
public static partial class DatabaseNameGuard
{
    public const string AllowedPattern = @"^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$";

    public static bool IsAllowed(string? databaseName) => databaseName is not null && AllowedName().IsMatch(databaseName);

    /// <summary>Throws <see cref="InvalidOperationException"/> unless tooling may create or drop the name.</summary>
    public static void EnsureAllowed(string? databaseName)
    {
        if (!IsAllowed(databaseName))
        {
            throw new InvalidOperationException(
                $"Refusing to create or drop database '{databaseName}': only names matching {AllowedPattern} are allowed.");
        }
    }

    // \z instead of $: '$' would also accept a trailing newline ("ControlHome_Dev\n").
    [GeneratedRegex(@"^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedName();
}
