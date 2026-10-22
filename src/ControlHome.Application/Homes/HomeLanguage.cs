using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Homes;

/// <summary>The home's language (<c>pt-BR</c> or <c>en</c>): what names a connector suggests are in (a flow's integration, a virtual home's devices).</summary>
public interface IHomeLanguage
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Reads it from the home (English before the setup created one).</summary>
public sealed class HomeLanguage(IControlHomeDb db) : IHomeLanguage
{
    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        await db.Homes.AsNoTracking().Select(h => h.Locale).FirstOrDefaultAsync(cancellationToken) ?? Locales.English;
}
