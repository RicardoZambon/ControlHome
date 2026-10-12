using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Auditing;

public static class AuditingServiceCollectionExtensions
{
    /// <summary>Registers the audit log reading services (writing is <see cref="IAuditLog"/>, from Infrastructure).</summary>
    public static IServiceCollection AddAuditing(this IServiceCollection services)
    {
        services.AddScoped<AuditQueryService>();
        return services;
    }
}
