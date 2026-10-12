using ControlHome.Application.Abstractions;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Infrastructure.Setup;

public static class SetupInfrastructureExtensions
{
    /// <summary>
    /// The setup code (<see cref="ISetupCodeProvider"/>, singleton), the exclusive transaction the setup completion runs
    /// in and the pre-database connection setup (<see cref="IDatabaseConnectionSetup"/>). Registering touches nothing; the
    /// code is generated when first resolved. A configured <c>Setup:Code</c> shorter than
    /// <see cref="SetupOptions.MinCodeLength"/> characters stops startup (the message never contains the code).
    /// </summary>
    public static IServiceCollection AddSetupInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<SetupOptions>()
            .BindConfiguration(SetupOptions.SectionName)
            .Validate(
                options => options.HasValidCode,
                $"{SetupOptions.SectionName}:{nameof(SetupOptions.Code)} must have at least {SetupOptions.MinCodeLength} characters "
                + "(dashes and spaces are not counted), or be empty for a random code.")
            .ValidateOnStart();
        services.TryAddSingleton<SetupCodeProvider>();
        services.TryAddSingleton<ISetupCodeProvider>(sp => sp.GetRequiredService<SetupCodeProvider>());
        services.TryAddScoped<IExclusiveTransaction, SqlExclusiveTransaction>();
        services.TryAddSingleton<IDatabaseConnectionSetup, DatabaseConnectionSetup>();
        return services;
    }

    /// <summary>Logs / writes the setup code at startup while setup is incomplete (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddSetupCodeAnnouncer(this IServiceCollection services)
    {
        services.AddHostedService<SetupCodeAnnouncer>();
        return services;
    }
}
