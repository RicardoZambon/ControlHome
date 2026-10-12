using System.Text.RegularExpressions;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// The single gate for every <c>CREATE DATABASE</c> / <c>DROP DATABASE</c> done by the app, tools or tests. The SQL
/// server also hosts other databases: only <c>ControlHome_(Dev|Test|Compose)[_suffix]</c> names (case-sensitive) are
/// allowed, plus the production name <c>ControlHome</c> on the app's own create-if-missing path.
/// </summary>
public static partial class DatabaseNameGuard
{
    /// <summary>The production database name; only the app's create-if-missing path may create it.</summary>
    public const string ProductionDatabaseName = "ControlHome";

    public const string AllowedPattern = @"^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$";

    public static bool IsAllowed(string? databaseName, bool allowProduction = false) =>
        databaseName is not null
        && (AllowedName().IsMatch(databaseName)
            || (allowProduction && string.Equals(databaseName, ProductionDatabaseName, StringComparison.Ordinal)));

    /// <summary>Throws <see cref="InvalidOperationException"/> unless the name may be created or dropped.</summary>
    /// <param name="allowProduction">True only for the app's own create-if-missing path.</param>
    public static void EnsureAllowed(string? databaseName, bool allowProduction = false)
    {
        if (!IsAllowed(databaseName, allowProduction))
        {
            throw new InvalidOperationException(
                $"Refusing to create or drop database '{databaseName}': only names matching {AllowedPattern}"
                + (allowProduction ? $" or '{ProductionDatabaseName}'" : string.Empty) + " are allowed.");
        }
    }

    // \z instead of $: '$' would also accept a trailing newline ("ControlHome_Dev\n").
    [GeneratedRegex(@"^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedName();
}
