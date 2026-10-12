using ControlHome.Domain.Auditing;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>Resets the app (<c>POST /api/_test/reset</c>) before every test, so tests sharing a factory stay independent.</summary>
public abstract class ResetAppTestBase<TFactory>(TFactory factory) : IAsyncLifetime
    where TFactory : WebApplicationFactory<Program>
{
    protected TFactory Factory { get; } = factory;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public virtual async ValueTask InitializeAsync() => await Factory.ResetAppAsync(Ct);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs a query against the app's database in a fresh scope.</summary>
    protected async Task<T> QueryDbAsync<T>(Func<ControlHomeDbContext, Task<T>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>());
    }

    /// <summary>Changes the app's database in a fresh scope (saved afterwards).</summary>
    protected async Task UpdateDbAsync(Func<ControlHomeDbContext, Task> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
        await change(db);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Audit entries with <paramref name="action"/>, oldest first.</summary>
    protected Task<List<AuditLogEntry>> AuditEntriesAsync(string action) =>
        QueryDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.Id).ToListAsync(Ct));
}
