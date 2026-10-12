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
    /// <param name="verifySucceeded">
    /// Asked only when the commit of a successful attempt failed with a transient error: the server may have committed even
    /// though the client saw an error. It must query the database (not the change tracker) for a trace only that attempt
    /// leaves, e.g. a row with an id generated before the call or the concurrency stamp the attempt wrote. True: the
    /// attempt's result is returned as is; false: the unit runs again. Without it, a unit that re-checks state would answer
    /// a committed success with a failure (e.g. "already completed").
    /// </param>
    Task<Result<T>> RunAsync<T>(
        string lockName,
        Func<CancellationToken, Task<Result<T>>> work,
        Func<CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken);
}
