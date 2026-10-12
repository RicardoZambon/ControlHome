using System.Xml.Linq;
using ControlHome.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace ControlHome.Infrastructure.Security;

/// <summary>
/// Fails fast while the database is not <see cref="DatabaseState.Ready"/> instead of letting EF Core retry a dead server
/// (the framework reads the key ring synchronously during startup, which would otherwise stall the host for minutes).
/// The framework retries on the next use, so the key ring loads as soon as the database is ready.
/// </summary>
internal sealed class ReadinessGatedXmlRepository(IXmlRepository inner, DatabaseReadiness readiness) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        EnsureReady();
        return inner.GetAllElements();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        EnsureReady();
        inner.StoreElement(element, friendlyName);
    }

    private void EnsureReady()
    {
        if (!readiness.IsReady)
        {
            throw new DatabaseNotReadyException(readiness.State);
        }
    }
}
