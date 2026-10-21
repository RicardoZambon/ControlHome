using System.Data;
using System.Text.Json;
using ControlHome.Domain.Devices;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// Writes write-behind batches to SQL Server in one transaction: current values with one <c>MERGE</c> into
/// <c>ch.AttributeState</c> (rows for attributes deleted meanwhile are skipped; an older observation never overwrites a newer
/// one), device availability / last-seen with one <c>UPDATE</c> (an orphaned device keeps the Unknown it was given when it
/// was orphaned: a buffered or late availability never overwrites it), and history appended to <c>ch.StateLog</c> (changes
/// of recorded attributes) and <c>ch.DeviceEvent</c> (momentary events) — one set-based <c>INSERT</c> each from a
/// table-valued parameter (types <c>ch.StateLogRow</c> / <c>ch.DeviceEventRow</c>), skipping attributes deleted meanwhile.
/// </summary>
public sealed class SqlStateStore(IDatabaseConnectionProvider connections, TimeProvider timeProvider) : IStateStore
{
    private const string InsertStateLog = """
        INSERT INTO [ch].[StateLog] (AttributeId, TsUtc, Num, Str)
        SELECT r.AttributeId, r.TsUtc, r.Num, r.Str
        FROM @rows AS r
        WHERE EXISTS (SELECT 1 FROM [ch].[DeviceAttributes] AS a WHERE a.Id = r.AttributeId);
        """;

    private const string InsertDeviceEvents = """
        INSERT INTO [ch].[DeviceEvent] (AttributeId, TsUtc, EventType, Payload)
        SELECT r.AttributeId, r.TsUtc, r.EventType, CAST(r.Payload AS json)
        FROM @rows AS r
        WHERE EXISTS (SELECT 1 FROM [ch].[DeviceAttributes] AS a WHERE a.Id = r.AttributeId);
        """;

    private static readonly SqlMetaData[] _stateLogRow =
    [
        new("AttributeId", SqlDbType.Int),
        new("TsUtc", SqlDbType.DateTime2, 0, 3),
        new("Num", SqlDbType.Float),
        new("Str", SqlDbType.NVarChar, StateLogEntry.MaxStringLength),
    ];

    private static readonly SqlMetaData[] _deviceEventRow =
    [
        new("AttributeId", SqlDbType.Int),
        new("TsUtc", SqlDbType.DateTime2, 0, 3),
        new("EventType", SqlDbType.NVarChar, DeviceEvent.MaxEventTypeLength),
        new("Payload", SqlDbType.NVarChar, SqlMetaData.Max),
    ];

    private const string MergeStates = """
        MERGE [ch].[AttributeState] WITH (HOLDLOCK) AS t
        USING (
            SELECT s.AttributeId, s.Kind, s.Num, s.Str, s.Json, s.ObservedAtUtc, s.LastChangedAtUtc
            FROM OPENJSON(@states) WITH (
                AttributeId int '$.a',
                Kind tinyint '$.k',
                Num float '$.n',
                Str nvarchar(255) '$.s',
                Json nvarchar(max) '$.j',
                ObservedAtUtc datetimeoffset '$.o',
                LastChangedAtUtc datetimeoffset '$.c') AS s
            WHERE EXISTS (SELECT 1 FROM [ch].[DeviceAttributes] AS a WHERE a.Id = s.AttributeId)
        ) AS src
        ON t.AttributeId = src.AttributeId
        WHEN MATCHED AND src.ObservedAtUtc >= t.ObservedAtUtc THEN
            UPDATE SET Kind = src.Kind, Num = src.Num, Str = src.Str, Json = CAST(src.Json AS json),
                       ObservedAtUtc = src.ObservedAtUtc, LastChangedAtUtc = src.LastChangedAtUtc, UpdatedAtUtc = @now
        WHEN NOT MATCHED THEN
            INSERT (AttributeId, Kind, Num, Str, Json, ObservedAtUtc, LastChangedAtUtc, UpdatedAtUtc)
            VALUES (src.AttributeId, src.Kind, src.Num, src.Str, CAST(src.Json AS json), src.ObservedAtUtc, src.LastChangedAtUtc, @now);
        """;

    private const string UpdateDevices = """
        UPDATE d
        SET Availability = CASE WHEN d.IsOrphaned = 1 THEN d.Availability ELSE COALESCE(s.Availability, d.Availability) END,
            LastSeenAtUtc = CASE WHEN s.LastSeenAtUtc IS NOT NULL AND (d.LastSeenAtUtc IS NULL OR s.LastSeenAtUtc > d.LastSeenAtUtc)
                                 THEN s.LastSeenAtUtc ELSE d.LastSeenAtUtc END
        FROM [ch].[Devices] AS d
        JOIN OPENJSON(@devices) WITH (
            DeviceId uniqueidentifier '$.d',
            Availability varchar(16) '$.a',
            LastSeenAtUtc datetimeoffset '$.l') AS s
            ON d.Id = s.DeviceId;
        """;

    public async Task WriteAsync(StateWriteBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.States.Count == 0 && batch.Devices.Count == 0 && batch.History.Count == 0)
        {
            return;
        }

        var connectionString = connections.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection is configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (batch.States.Count > 0)
        {
            await using var command = new SqlCommand(MergeStates, connection, transaction);
            command.Parameters.Add(new SqlParameter("@states", SqlDbType.NVarChar, -1) { Value = StatesJson(batch.States) });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = timeProvider.GetUtcNow() });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (batch.Devices.Count > 0)
        {
            await using var command = new SqlCommand(UpdateDevices, connection, transaction);
            command.Parameters.Add(new SqlParameter("@devices", SqlDbType.NVarChar, -1) { Value = DevicesJson(batch.Devices) });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stateRows = batch.History.Where(h => !h.IsEvent).ToList();
        if (stateRows.Count > 0)
        {
            await InsertRowsAsync(connection, transaction, InsertStateLog, "[ch].[StateLogRow]", StateLogRecords(stateRows), cancellationToken)
                .ConfigureAwait(false);
        }

        var eventRows = batch.History.Where(h => h.IsEvent).ToList();
        if (eventRows.Count > 0)
        {
            await InsertRowsAsync(connection, transaction, InsertDeviceEvents, "[ch].[DeviceEventRow]", DeviceEventRecords(eventRows), cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        string typeName,
        IEnumerable<SqlDataRecord> rows,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@rows", SqlDbType.Structured) { TypeName = typeName, Value = rows });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<SqlDataRecord> StateLogRecords(IEnumerable<PendingHistoryRow> rows)
    {
        foreach (var row in rows)
        {
            var (num, str) = StateLogEntry.Encode(row.Value);
            var record = new SqlDataRecord(_stateLogRow);
            record.SetInt32(0, row.AttributeId);
            record.SetDateTime(1, row.TsUtc.UtcDateTime);
            if (num is { } n)
            {
                record.SetDouble(2, n);
            }
            else
            {
                record.SetDBNull(2);
            }

            if (str is not null)
            {
                record.SetString(3, str);
            }
            else
            {
                record.SetDBNull(3);
            }

            yield return record;
        }
    }

    private static IEnumerable<SqlDataRecord> DeviceEventRecords(IEnumerable<PendingHistoryRow> rows)
    {
        foreach (var row in rows)
        {
            var record = new SqlDataRecord(_deviceEventRow);
            record.SetInt32(0, row.AttributeId);
            record.SetDateTime(1, row.TsUtc.UtcDateTime);
            var name = row.Value.S ?? string.Empty;
            record.SetString(2, name.Length <= DeviceEvent.MaxEventTypeLength ? name : name[..DeviceEvent.MaxEventTypeLength]);
            // Re-checked here, where the json CAST happens: one payload SQL Server rejects would fail the whole flush,
            // current state included, on every retry.
            if (DeviceEvent.StoredPayload(row.EventPayload) is { } payload)
            {
                record.SetString(3, payload);
            }
            else
            {
                record.SetDBNull(3);
            }

            yield return record;
        }
    }

    private static string StatesJson(IReadOnlyList<PendingAttributeState> states)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var state in states)
            {
                var (kind, num, str, json) = AttributeState.Encode(state.Value);
                writer.WriteStartObject();
                writer.WriteNumber("a", state.AttributeId);
                writer.WriteNumber("k", (byte)kind);
                if (num is { } n)
                {
                    writer.WriteNumber("n", n);
                }

                if (str is not null)
                {
                    writer.WriteString("s", str);
                }

                if (json is not null)
                {
                    writer.WriteString("j", json);
                }

                writer.WriteString("o", state.ObservedAtUtc);
                writer.WriteString("c", state.LastChangedAtUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string DevicesJson(IReadOnlyList<PendingDeviceUpdate> devices)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var device in devices)
            {
                writer.WriteStartObject();
                writer.WriteString("d", device.DeviceId);
                if (device.Availability is { } availability)
                {
                    writer.WriteString("a", availability.ToString());
                }

                if (device.LastSeenAtUtc is { } lastSeen)
                {
                    writer.WriteString("l", lastSeen);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
