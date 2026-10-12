using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Security;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the safety feature: the step-up PIN service, the in-memory grant/lockout store and the known-secrets
    /// registry used for log redaction.
    /// </summary>
    public static IServiceCollection AddSafety(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SafetyGrantStore>();
        services.TryAddSingleton<KnownSecrets>();
        services.AddScoped<StepUpService>();
        return services;
    }
}
