using FluentValidation;

namespace ControlHome.Api.Http;

public static class ValidationSetup
{
    /// <summary>Registers every validator in ControlHome.Application (stateless, so singletons).</summary>
    public static IServiceCollection AddControlHomeValidation(this IServiceCollection services)
    {
        // Messages are an English fallback (the SPA localizes by field); never switch with the server's culture.
        ValidatorOptions.Global.LanguageManager.Enabled = false;
        services.AddValidatorsFromAssemblyContaining<Application.AssemblyMarker>(ServiceLifetime.Singleton);
        return services;
    }
}
