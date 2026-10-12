using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// <see cref="IExclusiveTransaction"/> on SQL Server: <c>sp_getapplock(@Resource, 'Exclusive', 'Transaction')</c> inside a
/// transaction, all wrapped in the context's execution strategy (required because <c>EnableRetryOnFailure</c> is on, and it
/// makes a transient failure re-run the whole unit, lock included).
/// </summary>
public sealed class SqlExclusiveTransaction(ControlHomeDbContext db) : IExclusiveTransaction
{
    /// <summary>How long to wait for the lock before giving up (an exception, i.e. 500).</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private const int MaxLockNameLength = 255;

    public Task<Result<T>> RunAsync<T>(string lockName, Func<CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockName.Length, MaxLockNameLength, nameof(lockName));
        ArgumentNullException.ThrowIfNull(work);

        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            async ct =>
            {
                // A retried attempt must not see entities tracked (and maybe half-saved) by the failed one.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await AcquireLockAsync(lockName, ct);

                var result = await work(ct);
                if (result.IsSuccess)
                {
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                }

                return result;
            },
            cancellationToken);
    }

    private async Task AcquireLockAsync(string lockName, CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = (int)LockTimeout.TotalMilliseconds;
        var results = await db.Database.SqlQuery<int>($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {lockName}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {timeoutMilliseconds};
            SELECT @result AS [Value];
            """).ToListAsync(cancellationToken);

        // 0: granted, 1: granted after waiting; negative: timeout (-1), cancelled (-2), deadlock victim (-3), error (-999).
        var status = results.Count == 1 ? results[0] : int.MinValue;
        if (status < 0)
        {
            throw new TimeoutException(string.Create(
                CultureInfo.InvariantCulture,
                $"Could not acquire the application lock '{lockName}' (sp_getapplock returned {status})."));
        }
    }
}
