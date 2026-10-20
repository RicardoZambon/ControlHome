using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

public static class DeviceRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// The device runtime: registry, state cache, state processor, write-behind, inventory, integration supervisor (also the
    /// <see cref="ICommandDispatcher"/> and <see cref="IIntegrationRuntimeControl"/>), runtime lease and secret protection.
    /// Replaces the Application defaults (database state and device-status readers, no-op dispatcher). Nothing starts until
    /// <see cref="AddDeviceRuntimeHost"/>'s host does. Connectors are registered separately with <see cref="AddConnector{T}"/>.
    /// </summary>
    public static IServiceCollection AddDeviceRuntime(this IServiceCollection services)
    {
        services.AddOptions<DeviceRuntimeOptions>().BindConfiguration(DeviceRuntimeOptions.SectionName);
        services.AddDataProtection();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new RuntimeClock(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<KnownSecrets>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<IRealtimePublisher>(NullRealtimePublisher.Instance);

        services.TryAddSingleton<DeviceRuntimeState>();
        services.Replace(ServiceDescriptor.Singleton<IRuntimeStatus>(sp => sp.GetRequiredService<DeviceRuntimeState>()));
        services.TryAddSingleton<DeviceRegistry>();
        services.TryAddSingleton<StateCache>();
        services.TryAddSingleton<RealtimeBatcher>();
        services.TryAddSingleton<IStateStore, SqlStateStore>();
        services.TryAddSingleton<StateWriteBehind>();
        services.TryAddSingleton<DeviceInventoryService>();
        services.TryAddSingleton<IDeviceInventory>(sp => sp.GetRequiredService<DeviceInventoryService>());
        services.TryAddSingleton(sp => new StateProcessor(
            sp.GetRequiredService<DeviceRegistry>(),
            sp.GetRequiredService<StateCache>(),
            sp.GetRequiredService<StateWriteBehind>(),
            sp.GetRequiredService<IDeviceInventory>(),
            sp.GetRequiredService<RealtimeBatcher>(),
            // Resolved when the processor starts: subscribers (the command tracker) depend on the dispatcher, which needs the processor.
            () => sp.GetServices<IStateChangeSubscriber>(),
            sp.GetRequiredService<RuntimeClock>(),
            sp.GetRequiredService<IOptions<DeviceRuntimeOptions>>(),
            sp.GetRequiredService<ILogger<StateProcessor>>()));
        services.TryAddSingleton<IntegrationSecretProtector>();
        services.Replace(ServiceDescriptor.Singleton<IIntegrationSecretProtector>(sp => sp.GetRequiredService<IntegrationSecretProtector>()));
        services.TryAddSingleton<IntegrationSupervisor>();
        services.Replace(ServiceDescriptor.Singleton<ICommandDispatcher>(sp => sp.GetRequiredService<IntegrationSupervisor>()));
        services.Replace(ServiceDescriptor.Singleton<IIntegrationRuntimeControl>(sp => sp.GetRequiredService<IntegrationSupervisor>()));
        services.Replace(ServiceDescriptor.Scoped<IAttributeStateReader, RuntimeAttributeStateReader>());
        services.Replace(ServiceDescriptor.Singleton<IDeviceStatusReader, RuntimeDeviceStatusReader>());
        services.TryAddSingleton<RuntimeLease>();
        services.TryAddSingleton<DeviceRuntime>();
        return services;
    }

    /// <summary>Runs the device runtime with the host (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddDeviceRuntimeHost(this IServiceCollection services)
    {
        services.AddHostedService<DeviceRuntimeHost>();
        return services;
    }

    /// <summary>Registers a connector statically (connectors are never loaded as plugins).</summary>
    public static IServiceCollection AddConnector<TFactory>(this IServiceCollection services)
        where TFactory : class, IConnectorFactory
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorFactory, TFactory>());
        return services;
    }
}
