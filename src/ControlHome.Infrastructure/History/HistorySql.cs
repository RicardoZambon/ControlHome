using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ControlHome.Domain.History;
using Microsoft.Data.SqlClient;

namespace ControlHome.Infrastructure.History;

/// <summary>The readings of one attribute around a window: the last one before it, those in it, and the first one after it.</summary>
internal sealed class WindowReadings
{
    public Reading? Before { get; set; }

    public List<Reading> Within { get; } = [];

    public Reading? After { get; set; }
}

/// <summary>A row of <c>StatShortTerm</c> / <c>StatHourly</c> to merge.</summary>
internal sealed record StatisticRow(int AttributeId, DateTimeOffset BucketStartUtc, double? Mean, double? Min, double? Max, double? Sum, double? LastCounter);

/// <summary>A row of <c>EnergyHourly</c> to merge.</summary>
internal sealed record EnergyRow(Guid EndpointId, DateTimeOffset HourStartUtc, double Wh, EnergySource Source, bool Estimated);

/// <summary>
/// The SQL of the history rollups (ADO.NET on one connection and transaction; set-based reads and <c>MERGE</c>s with
/// <c>OPENJSON</c> parameters). Table names come from constants only.
/// </summary>
internal static class HistorySql
{
    public const string ShortTermTable = "[ch].[StatShortTerm]";
    public const string HourlyTable = "[ch].[StatHourly]";

    private const string ReadReadings = """
        DECLARE @ids TABLE (AttributeId int PRIMARY KEY, KnownOnly bit NOT NULL);
        INSERT INTO @ids (AttributeId, KnownOnly) SELECT a, k FROM OPENJSON(@attributes) WITH (a int '$.a', k bit '$.k');

        SELECT i.AttributeId, c.TsUtc, c.Num, CAST(0 AS tinyint) AS Part
        FROM @ids AS i
        CROSS APPLY (
            SELECT TOP (1) s.TsUtc, s.Num FROM [ch].[StateLog] AS s
            WHERE s.AttributeId = i.AttributeId AND s.TsUtc < @start AND (i.KnownOnly = 0 OR s.Num IS NOT NULL)
            ORDER BY s.TsUtc DESC) AS c
        UNION ALL
        SELECT s.AttributeId, s.TsUtc, s.Num, CAST(1 AS tinyint)
        FROM @ids AS i
        JOIN [ch].[StateLog] AS s ON s.AttributeId = i.AttributeId AND s.TsUtc >= @start AND s.TsUtc < @end
        UNION ALL
        SELECT i.AttributeId, n.TsUtc, n.Num, CAST(2 AS tinyint)
        FROM @ids AS i
        CROSS APPLY (
            SELECT TOP (1) s.TsUtc, s.Num FROM [ch].[StateLog] AS s
            WHERE @withAfter = 1 AND s.AttributeId = i.AttributeId AND s.TsUtc >= @end
            ORDER BY s.TsUtc ASC) AS n
        ORDER BY AttributeId, Part, TsUtc;
        """;

    private const string MergeStatistics = """
        DECLARE @ids TABLE (AttributeId int PRIMARY KEY);
        INSERT INTO @ids (AttributeId) SELECT DISTINCT value FROM OPENJSON(@attributes) WITH (value int '$');

        WITH target AS (
            SELECT t.* FROM {0} AS t
            WHERE t.BucketStartUtc >= @start AND t.BucketStartUtc < @end
              AND t.AttributeId IN (SELECT AttributeId FROM @ids))
        MERGE target AS t
        USING (
            SELECT a AS AttributeId, b AS BucketStartUtc, mean AS Mean, mn AS Min, mx AS Max, total AS Sum, last AS LastCounter
            FROM OPENJSON(@rows) WITH (
                a int '$.a', b datetime2(0) '$.b', mean float '$.mean', mn float '$.min', mx float '$.max',
                total float '$.sum', last float '$.last')) AS s
        ON t.AttributeId = s.AttributeId AND t.BucketStartUtc = s.BucketStartUtc
        WHEN MATCHED THEN
            UPDATE SET Mean = s.Mean, Min = s.Min, Max = s.Max, Sum = s.Sum, LastCounter = s.LastCounter
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (AttributeId, BucketStartUtc, Mean, Min, Max, Sum, LastCounter)
            VALUES (s.AttributeId, s.BucketStartUtc, s.Mean, s.Min, s.Max, s.Sum, s.LastCounter)
        WHEN NOT MATCHED BY SOURCE THEN
            DELETE;
        """;

    private const string MergeEnergy = """
        DECLARE @endpoints TABLE (EndpointId uniqueidentifier PRIMARY KEY);
        INSERT INTO @endpoints (EndpointId) SELECT DISTINCT value FROM OPENJSON(@endpointIds) WITH (value uniqueidentifier '$');

        WITH target AS (
            SELECT t.* FROM [ch].[EnergyHourly] AS t
            WHERE t.HourStartUtc >= @start AND t.HourStartUtc < @end
              AND t.EndpointId IN (SELECT EndpointId FROM @endpoints))
        MERGE target AS t
        USING (
            SELECT e AS EndpointId, h AS HourStartUtc, wh AS Wh, src AS Source, est AS Estimated
            FROM OPENJSON(@rows) WITH (e uniqueidentifier '$.e', h datetime2(0) '$.h', wh float '$.wh', src varchar(16) '$.src', est bit '$.est')) AS s
        ON t.EndpointId = s.EndpointId AND t.HourStartUtc = s.HourStartUtc
        WHEN MATCHED THEN
            UPDATE SET Wh = s.Wh, Source = s.Source, Estimated = s.Estimated
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (EndpointId, HourStartUtc, Wh, Source, Estimated) VALUES (s.EndpointId, s.HourStartUtc, s.Wh, s.Source, s.Estimated)
        WHEN NOT MATCHED BY SOURCE THEN
            DELETE;
        """;

    /// <summary>Upserts the given hours only (vendor energy windows): other hours of the endpoints are left as they are.</summary>
    private const string UpsertEnergy = """
        MERGE [ch].[EnergyHourly] WITH (HOLDLOCK) AS t
        USING (
            SELECT e AS EndpointId, h AS HourStartUtc, wh AS Wh, src AS Source, est AS Estimated
            FROM OPENJSON(@rows) WITH (e uniqueidentifier '$.e', h datetime2(0) '$.h', wh float '$.wh', src varchar(16) '$.src', est bit '$.est')) AS s
        ON t.EndpointId = s.EndpointId AND t.HourStartUtc = s.HourStartUtc
        WHEN MATCHED THEN
            UPDATE SET Wh = s.Wh, Source = s.Source, Estimated = s.Estimated
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (EndpointId, HourStartUtc, Wh, Source, Estimated) VALUES (s.EndpointId, s.HourStartUtc, s.Wh, s.Source, s.Estimated);
        """;

    private const string SetWatermark = """
        UPDATE [ch].[RollupProgress] SET WatermarkUtc = @watermark, UpdatedAtUtc = @now WHERE Name = @name;
        IF @@ROWCOUNT = 0
            INSERT INTO [ch].[RollupProgress] (Name, WatermarkUtc, UpdatedAtUtc) VALUES (@name, @watermark, @now);
        """;

    public static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, TimeSpan timeout) =>
        new(sql, connection, transaction) { CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) };

    public static SqlParameter Time(string name, DateTimeOffset value) =>
        new(name, SqlDbType.DateTime2) { Scale = 3, Value = value.UtcDateTime };

    public static DateTimeOffset ReadTime(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    public static async Task<DateTimeOffset?> GetWatermarkAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT WatermarkUtc FROM [ch].[RollupProgress] WHERE Name = @name;", timeout);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, RollupProgress.NameMaxLength) { Value = name });
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;
    }

    public static async Task SetWatermarkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string name,
        DateTimeOffset watermark,
        DateTimeOffset now,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, SetWatermark, timeout);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, RollupProgress.NameMaxLength) { Value = name });
        command.Parameters.Add(Time("@watermark", watermark));
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The oldest <c>StateLog</c> observation, or <c>null</c> when it is empty.</summary>
    public static async Task<DateTimeOffset?> GetFirstReadingAsync(SqlConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, "SELECT MIN(TsUtc) FROM [ch].[StateLog];", timeout);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;
    }

    /// <summary>
    /// Per attribute: the last reading before <paramref name="start"/> (only a known value when the attribute's flag is
    /// set), the readings in <c>[start, end)</c> and — when <paramref name="withAfter"/> — the first at or after
    /// <paramref name="end"/>.
    /// </summary>
    public static async Task<Dictionary<int, WindowReadings>> ReadReadingsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<(int AttributeId, bool KnownOnly)> attributes,
        DateTimeOffset start,
        DateTimeOffset end,
        bool withAfter,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = attributes.ToDictionary(a => a.AttributeId, _ => new WindowReadings());
        if (attributes.Count == 0)
        {
            return result;
        }

        await using var command = Command(connection, transaction, ReadReadings, timeout);
        command.Parameters.Add(Json("@attributes", attributes.Select(a => new { a = a.AttributeId, k = a.KnownOnly })));
        command.Parameters.Add(Time("@start", start));
        command.Parameters.Add(Time("@end", end));
        command.Parameters.Add(new SqlParameter("@withAfter", SqlDbType.Bit) { Value = withAfter });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var readings = result[reader.GetInt32(0)];
            var reading = new Reading(ReadTime(reader, 1), reader.IsDBNull(2) ? null : reader.GetDouble(2));
            switch (reader.GetByte(3))
            {
                case 0:
                    readings.Before = reading;
                    break;
                case 1:
                    readings.Within.Add(reading);
                    break;
                default:
                    readings.After = reading;
                    break;
            }
        }

        return result;
    }

    /// <summary>Statistics rows of <paramref name="table"/> in <c>[start, end)</c> (all attributes, or the given ones), oldest first.</summary>
    public static async Task<List<StatisticRow>> ReadStatisticsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyCollection<int>? attributeIds,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var filter = attributeIds is null ? string.Empty : " AND AttributeId IN (SELECT value FROM OPENJSON(@attributes) WITH (value int '$'))";
        var sql = string.Format(
            CultureInfo.InvariantCulture,
            "SELECT AttributeId, BucketStartUtc, Mean, Min, Max, Sum, LastCounter FROM {0} " +
            "WHERE BucketStartUtc >= @start AND BucketStartUtc < @end{1} ORDER BY AttributeId, BucketStartUtc;",
            table,
            filter);
        await using var command = Command(connection, transaction, sql, timeout);
        command.Parameters.Add(Time("@start", start));
        command.Parameters.Add(Time("@end", end));
        if (attributeIds is not null)
        {
            command.Parameters.Add(Json("@attributes", attributeIds));
        }

        var rows = new List<StatisticRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StatisticRow(
                reader.GetInt32(0),
                ReadTime(reader, 1),
                NullableDouble(reader, 2),
                NullableDouble(reader, 3),
                NullableDouble(reader, 4),
                NullableDouble(reader, 5),
                NullableDouble(reader, 6)));
        }

        return rows;
    }

    /// <summary>
    /// Replaces the rows of <paramref name="attributeIds"/> in <c>[start, end)</c> of <paramref name="table"/> by
    /// <paramref name="rows"/> (one <c>MERGE</c>: re-running a window gives the same rows).
    /// </summary>
    public static async Task MergeStatisticsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyCollection<int> attributeIds,
        IReadOnlyCollection<StatisticRow> rows,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (attributeIds.Count == 0)
        {
            return;
        }

        await using var command = Command(connection, transaction, string.Format(CultureInfo.InvariantCulture, MergeStatistics, table), timeout);
        command.Parameters.Add(Json("@attributes", attributeIds));
        command.Parameters.Add(new SqlParameter("@rows", SqlDbType.NVarChar, -1) { Value = StatisticsJson(rows) });
        command.Parameters.Add(Time("@start", start));
        command.Parameters.Add(Time("@end", end));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the <c>EnergyHourly</c> rows of <paramref name="endpointIds"/> in <c>[start, end)</c> by <paramref name="rows"/>.</summary>
    public static async Task MergeEnergyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<Guid> endpointIds,
        IReadOnlyCollection<EnergyRow> rows,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (endpointIds.Count == 0)
        {
            return;
        }

        await using var command = Command(connection, transaction, MergeEnergy, timeout);
        command.Parameters.Add(Json("@endpointIds", endpointIds));
        command.Parameters.Add(new SqlParameter("@rows", SqlDbType.NVarChar, -1) { Value = EnergyJson(rows) });
        command.Parameters.Add(Time("@start", start));
        command.Parameters.Add(Time("@end", end));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts or replaces exactly the <c>EnergyHourly</c> rows in <paramref name="rows"/> (no other row changes).</summary>
    public static async Task UpsertEnergyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<EnergyRow> rows,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var command = Command(connection, transaction, UpsertEnergy, timeout);
        command.Parameters.Add(new SqlParameter("@rows", SqlDbType.NVarChar, -1) { Value = EnergyJson(rows) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static SqlParameter Json<T>(string name, T value) =>
        new(name, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(value) };

    private static double? NullableDouble(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static string StatisticsJson(IEnumerable<StatisticRow> rows) => WriteArray(rows, (writer, row) =>
    {
        writer.WriteNumber("a", row.AttributeId);
        writer.WriteString("b", BucketText(row.BucketStartUtc));
        WriteOptional(writer, "mean", row.Mean);
        WriteOptional(writer, "min", row.Min);
        WriteOptional(writer, "max", row.Max);
        WriteOptional(writer, "sum", row.Sum);
        WriteOptional(writer, "last", row.LastCounter);
    });

    private static string EnergyJson(IEnumerable<EnergyRow> rows) => WriteArray(rows, (writer, row) =>
    {
        writer.WriteString("e", row.EndpointId);
        writer.WriteString("h", BucketText(row.HourStartUtc));
        writer.WriteNumber("wh", row.Wh);
        writer.WriteString("src", EnergySources.Name(row.Source));
        writer.WriteBoolean("est", row.Estimated);
    });

    /// <summary>A UTC bucket start as OPENJSON reads it into <c>datetime2(0)</c>.</summary>
    private static string BucketText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static void WriteOptional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumber(name, number);
        }
    }

    private static string WriteArray<T>(IEnumerable<T> rows, Action<Utf8JsonWriter, T> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                write(writer, row);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
