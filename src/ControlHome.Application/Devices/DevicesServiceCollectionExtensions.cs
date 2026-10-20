using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Devices;

public static class DevicesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the devices feature's application services: <see cref="DeviceQueryService"/>, <see cref="DeviceAdminService"/>,
    /// <see cref="DeviceCommandService"/> with its singleton <see cref="CommandTracker"/> (also an
    /// <see cref="IStateChangeSubscriber"/>, for command confirmations), and — unless something else is already registered —
    /// the database-backed <see cref="IAttributeStateReader"/>, a do-nothing <see cref="ICommandDispatcher"/> and a
    /// do-nothing <see cref="IRealtimePublisher"/> (the device runtime and the hub replace them).
    /// </summary>
    public static IServiceCollection AddDevices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SafetyGrantStore>();
        services.TryAddSingleton<KnownSecrets>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICommandDispatcher>(NoDeviceRuntime.Instance);
        services.TryAddSingleton<IRealtimePublisher>(NullRealtimePublisher.Instance);
        services.TryAddScoped<IAttributeStateReader, DbAttributeStateReader>();
        services.TryAddSingleton<IDeviceStatusReader>(NoLiveDeviceStatus.Instance);

        services.AddScoped<DeviceQueryService>();
        services.AddScoped<DeviceAdminService>();
        services.AddScoped<DeviceCommandService>();
        services.TryAddSingleton<CommandTracker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStateChangeSubscriber, CommandTrackerSubscriber>());
        return services;
    }

    /// <summary>Forwards state changes to the singleton <see cref="CommandTracker"/>.</summary>
    private sealed class CommandTrackerSubscriber(CommandTracker tracker) : IStateChangeSubscriber
    {
        public ValueTask OnStateChangedAsync(IReadOnlyList<AttributeStateChange> changes, CancellationToken cancellationToken) =>
            tracker.OnStateChangedAsync(changes, cancellationToken);
    }
}
