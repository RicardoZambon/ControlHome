using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Devices;

public static class DevicesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the devices feature's application services: <see cref="DeviceQueryService"/> and, unless another one is
    /// already registered (the runtime's state cache), the database-backed <see cref="IAttributeStateReader"/>.
    /// </summary>
    public static IServiceCollection AddDevices(this IServiceCollection services)
    {
        services.AddScoped<DeviceQueryService>();
        services.TryAddScoped<IAttributeStateReader, DbAttributeStateReader>();
        return services;
    }
}
