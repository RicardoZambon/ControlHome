using ControlHome.Application.Common;

namespace ControlHome.Application.Abstractions;

/// <summary>
/// Runs a unit of work in one database transaction while holding an exclusive, named application lock (SQL Server
/// <c>sp_getapplock</c>, owner <c>Transaction</c>), so concurrent requests for the same lock run one after the other.
/// </summary>
/// <remarks>
/// The transaction commits only when the work returns a success; a failure rolls it back. The work may be re-run on a
/// transient database error (the context's change tracker is cleared before every attempt), so it must load everything it
/// changes inside the delegate and must not have side effects outside the database.
/// </remarks>
public interface IExclusiveTransaction
{
    Task<Result<T>> RunAsync<T>(string lockName, Func<CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken);
}
