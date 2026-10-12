namespace ControlHome.Application.Abstractions;

/// <summary>Read-only view of the database's startup state (implemented by Infrastructure).</summary>
public interface IDatabaseStatus
{
    /// <summary>Whether a connection string is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the database is reachable, migrated and seeded.</summary>
    bool IsReady { get; }

    /// <summary>The current state name: <c>NotConfigured</c>, <c>Connecting</c>, <c>Migrating</c>, <c>Ready</c> or <c>Failed</c>.</summary>
    string State { get; }

    /// <summary>The error code of the last failure (e.g. <c>db.connection_failed</c>), if any.</summary>
    string? LastError { get; }
}
