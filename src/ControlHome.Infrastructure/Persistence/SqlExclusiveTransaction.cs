using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// <see cref="IExclusiveTransaction"/> on SQL Server: <c>sp_getapplock(@Resource, 'Exclusive', 'Transaction')</c> inside a
/// transaction, all wrapped in the context's execution strategy (required because <c>EnableRetryOnFailure</c> is on, and it
/// makes a transient failure re-run the whole unit, lock included). When the commit itself fails transiently, the caller's
/// <c>verifySucceeded</c> decides whether the server committed anyway (then that attempt's result is returned) or the unit
/// runs again.
/// </summary>
public sealed class SqlExclusiveTransaction(ControlHomeDbContext db) : IExclusiveTransaction
{
    /// <summary>How long to wait for the lock before giving up (<see cref="ApplicationLockException"/>, i.e. 500).</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private const int MaxLockNameLength = 255;

    public Task<Result<T>> RunAsync<T>(
        string lockName,
        Func<CancellationToken, Task<Result<T>>> work,
        Func<CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockName.Length, MaxLockNameLength, nameof(lockName));
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(verifySucceeded);

        // The successful result of the attempt whose commit was sent (null while no commit is in flight).
        Result<T>? committing = null;
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync<object?, Result<T>>(
            state: null,
            operation: async (_, _, ct) =>
            {
                committing = null;

                // A retried attempt must not see entities tracked (and maybe half-saved) by the failed one.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await AcquireLockAsync(lockName, ct);

                var result = await work(ct);
                if (result.IsSuccess)
                {
                    committing = result;
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                }

                return result;
            },
            verifySucceeded: async (_, _, ct) =>
                committing is { } attempted && await verifySucceeded(ct)
                    ? new ExecutionResult<Result<T>>(successful: true, attempted)
                    : new ExecutionResult<Result<T>>(successful: false, result: null!),
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
        // Not a TimeoutException: the execution strategy treats that as transient and would wait the full timeout again.
        var status = results.Count == 1 ? results[0] : int.MinValue;
        if (status < 0)
        {
            throw new ApplicationLockException(string.Create(
                CultureInfo.InvariantCulture,
                $"Could not acquire the application lock '{lockName}' (sp_getapplock returned {status})."));
        }
    }
}
