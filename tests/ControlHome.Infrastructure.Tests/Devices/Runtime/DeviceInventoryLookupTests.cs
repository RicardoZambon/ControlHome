using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static ControlHome.Infrastructure.Tests.Devices.Runtime.PipelineHarness;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

/// <summary>
/// <see cref="DeviceInventoryService.EnsureAttributeAsync"/> runs on the state pipeline's loop: whatever the registry can
/// answer never touches the database (here: a scope factory that throws).
/// </summary>
public sealed class DeviceInventoryLookupTests : IDisposable
{
    private readonly Guid _integrationId = Guid.CreateVersion7();
    private readonly DeviceRegistry _registry = new();
    private readonly RealtimeBatcher _batcher;
    private readonly DeviceInventoryService _inventory;

    public DeviceInventoryLookupTests()
    {
        var noDatabase = Substitute.For<IServiceScopeFactory>();
        noDatabase.CreateScope().Returns(_ => throw new InvalidOperationException("The database must not be asked."));
        _batcher = new RealtimeBatcher(NullRealtimePublisher.Instance, TimeProvider.System, Options.Create(new DeviceRuntimeOptions()), NullLogger<RealtimeBatcher>.Instance);
        _inventory = new DeviceInventoryService(noDatabase, _registry, _batcher, new RuntimeClock(TimeProvider.System), NullLogger<DeviceInventoryService>.Instance);

        var light = Light(_integrationId, Guid.CreateVersion7());
        light.Endpoints.Single().Capabilities.Add(new EndpointCapability
        {
            Id = 13,
            CapabilityId = CapabilityIds.Thermostat,
            Attributes = [new DeviceAttribute { Id = 4, Key = "mode" }],
        });
        _registry.Register(light);
    }

    public void Dispose() => _batcher.Dispose();

    [Theory]
    [InlineData("other-device", Endpoint.MainKey, CapabilityIds.Switch, "on")]
    [InlineData(DeviceExternalId, "ch2", CapabilityIds.Switch, "on")]
    [InlineData(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Lock, "lockState")]
    [InlineData(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "vendorKey")]
    public async Task Unknown_devices_endpoints_capabilities_and_keys_are_answered_from_memory(
        string device,
        string endpoint,
        string capability,
        string attribute)
    {
        var address = new AttributeAddress(_integrationId, device, endpoint, capability, attribute);

        (await _inventory.EnsureAttributeAsync(address, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Only_a_key_the_definition_has_without_a_row_goes_to_the_database()
    {
        var address = new AttributeAddress(_integrationId, DeviceExternalId, Endpoint.MainKey, CapabilityIds.Thermostat, "targetTemperature");

        // The failure surfaces (the pipeline backs off and retries); it is never mistaken for "unknown".
        await Should.ThrowAsync<InvalidOperationException>(() => _inventory.EnsureAttributeAsync(address, TestContext.Current.CancellationToken));
    }
}
