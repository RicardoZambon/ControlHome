namespace ControlHome.Application.Abstractions;

/// <summary>Read-only view of the database's startup state (implemented by Infrastructure).</summary>
public interface IDatabaseStatus
{
    /// <summary>Whether a connection string is configured at all (either source).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Where the connection string comes from: <c>environment</c> (configuration, e.g. <c>ConnectionStrings__ControlHome</c>),
    /// <c>file</c> (saved by the setup wizard) or <c>none</c>.
    /// </summary>
    string Source { get; }

    /// <summary>Whether the database is reachable, migrated and seeded.</summary>
    bool IsReady { get; }

    /// <summary>The current state name: <c>NotConfigured</c>, <c>Connecting</c>, <c>Migrating</c>, <c>Ready</c> or <c>Failed</c>.</summary>
    string State { get; }

    /// <summary>The error code of the last failure (e.g. <c>db.connection_failed</c>), if any.</summary>
    string? LastError { get; }
}
