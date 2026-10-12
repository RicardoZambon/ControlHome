using System.Data.Common;
using System.Security.Cryptography;
using ControlHome.Application.Common;
using ControlHome.Domain.Auditing;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Infrastructure.Tests;

/// <summary>
/// <see cref="SqlExclusiveTransaction"/> under transient commit failures (simulated with a <see cref="TimeoutException"/>,
/// which the SQL Server execution strategy treats as transient): a commit that reached the server is recognized through
/// <c>verifySucceeded</c> instead of being run again (which would answer its own success with a "duplicate" failure), and a
/// commit that never happened is retried.
/// </summary>
public sealed class SqlExclusiveTransactionTests(SqlServerFixture sql)
{
    private const string LockName = "ControlHome.Test.Exclusive";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_commit_the_server_applied_but_the_client_saw_fail_returns_that_attempts_result()
    {
        var fault = new CommitFaultInterceptor { FailAfterCommit = true };
        await RunAgainstFreshDatabaseAsync(fault, async db =>
        {
            var marker = Marker();
            var runs = 0;

            var result = await new SqlExclusiveTransaction(db).RunAsync(
                LockName,
                token => AddMarkerOnceAsync(db, marker, () => ++runs, token),
                token => MarkerExistsAsync(db, marker, token),
                Ct);

            fault.Faults.ShouldBe(1);
            result.IsSuccess.ShouldBeTrue(result.Error?.Code);
            result.Value.ShouldBe(1);
            runs.ShouldBe(1); // not re-run (a re-run would have answered "already done")
            (await db.AuditLog.AsNoTracking().CountAsync(e => e.Action == marker, Ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task A_commit_that_never_reached_the_server_is_run_again()
    {
        var fault = new CommitFaultInterceptor { FailBeforeCommit = true };
        await RunAgainstFreshDatabaseAsync(fault, async db =>
        {
            var marker = Marker();
            var runs = 0;

            var result = await new SqlExclusiveTransaction(db).RunAsync(
                LockName,
                token => AddMarkerOnceAsync(db, marker, () => ++runs, token),
                token => MarkerExistsAsync(db, marker, token),
                Ct);

            fault.Faults.ShouldBe(1);
            result.IsSuccess.ShouldBeTrue(result.Error?.Code);
            runs.ShouldBe(2);
            (await db.AuditLog.AsNoTracking().CountAsync(e => e.Action == marker, Ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task A_failed_unit_is_rolled_back()
    {
        await RunAgainstFreshDatabaseAsync(new CommitFaultInterceptor(), async db =>
        {
            var marker = Marker();

            var result = await new SqlExclusiveTransaction(db).RunAsync<int>(
                LockName,
                async token =>
                {
                    db.AuditLog.Add(Entry(marker));
                    await db.SaveChangesAsync(token);
                    return AppError.Conflict("test.refused", "Refused after writing.");
                },
                _ => Task.FromResult(false),
                Ct);

            result.IsFailure.ShouldBeTrue();
            (await db.AuditLog.AsNoTracking().CountAsync(e => e.Action == marker, Ct)).ShouldBe(0);
        });
    }

    private static string Marker() => $"test.{Guid.NewGuid():N}";

    private static AuditLogEntry Entry(string action) =>
        new() { Action = action, ActorType = AuditActorType.System, OccurredAtUtc = DateTimeOffset.UnixEpoch };

    /// <summary>A unit that re-checks its own state first, like the setup completion: done already → a failure.</summary>
    private static async Task<Result<int>> AddMarkerOnceAsync(ControlHomeDbContext db, string marker, Func<int> countRun, CancellationToken token)
    {
        var run = countRun();
        if (await db.AuditLog.AnyAsync(e => e.Action == marker, token))
        {
            return AppError.Conflict("test.already_done", "Already done.");
        }

        db.AuditLog.Add(Entry(marker));
        await db.SaveChangesAsync(token);
        return run;
    }

    private static Task<bool> MarkerExistsAsync(ControlHomeDbContext db, string marker, CancellationToken token) =>
        db.AuditLog.AsNoTracking().AnyAsync(e => e.Action == marker, token);

    private async Task RunAgainstFreshDatabaseAsync(CommitFaultInterceptor fault, Func<ControlHomeDbContext, Task> test)
    {
        await sql.EnsureStartedAsync();
        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        await using var services = InfrastructureHost.Build(
            new Dictionary<string, string?> { ["ConnectionStrings:ControlHome"] = sql.CreateDatabaseConnectionString(suffix) },
            s => s.ConfigureDbContext<ControlHomeDbContext>((_, options) => options.AddInterceptors(fault)));
        try
        {
            var initializer = services.GetRequiredService<DatabaseInitializer>();
            await initializer.StartAsync(Ct);
            await initializer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(120), Ct);
            services.GetRequiredService<DatabaseReadiness>().State.ShouldBe(DatabaseState.Ready);

            // Armed only now: the migrations commit transactions too.
            fault.Armed = true;
            await using (var scope = services.CreateAsyncScope())
            {
                await test(scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>());
            }

            await initializer.StopAsync(Ct);
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }

    /// <summary>Throws a transient error once, just before or just after a commit is sent.</summary>
    private sealed class CommitFaultInterceptor : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public bool FailBeforeCommit { get; init; }

        public bool FailAfterCommit { get; init; }

        public int Faults { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && FailBeforeCommit && Faults == 0)
            {
                Faults++;
                throw new TimeoutException("Simulated: the connection broke before the commit was sent.");
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && FailAfterCommit && Faults == 0)
            {
                Faults++;
                throw new TimeoutException("Simulated: the server committed, but its acknowledgement was lost.");
            }

            return Task.CompletedTask;
        }
    }
}
