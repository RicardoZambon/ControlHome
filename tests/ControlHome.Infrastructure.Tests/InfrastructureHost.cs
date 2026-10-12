using ControlHome.Application.Abstractions;
using ControlHome.Infrastructure.Auditing;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ControlHome.Infrastructure.Tests;

/// <summary>A minimal service provider with the Infrastructure registrations (no web host).</summary>
internal static class InfrastructureHost
{
    public static ServiceProvider Build(
        IDictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ICurrentActor>(SystemActor.Instance);
        services.AddControlHomeInfrastructure();
        services.AddSingleton<DatabaseInitializer>();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "ControlHome.Infrastructure.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
