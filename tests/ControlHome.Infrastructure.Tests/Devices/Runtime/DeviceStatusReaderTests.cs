using ControlHome.Application.Runtime;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

/// <summary>Live device status for REST reads: the registry's view, but only while this process is the active runtime.</summary>
public sealed class DeviceStatusReaderTests
{
    [Fact]
    public async Task The_registry_is_served_only_while_the_runtime_is_active()
    {
        await using var harness = new PipelineHarness();
        var device = harness.Registry.FindDevice(harness.DeviceId)!;
        device.Availability = Availability.Online;
        device.LastSeenAtUtc = PipelineHarness.T0;
        var reader = new RuntimeDeviceStatusReader(harness.Registry, harness.RuntimeState);
        var unknownDevice = Guid.CreateVersion7();

        // Starting or standby: the registry is not this process's live view (the database values are used).
        reader.Get([harness.DeviceId]).ShouldBeEmpty();
        harness.RuntimeState.Mode = RuntimeMode.Standby;
        reader.Get([harness.DeviceId]).ShouldBeEmpty();

        harness.RuntimeState.Mode = RuntimeMode.Active;
        var live = reader.Get([harness.DeviceId, unknownDevice]);

        live.Keys.ShouldBe([harness.DeviceId]);
        live[harness.DeviceId].Availability.ShouldBe(Availability.Online);
        live[harness.DeviceId].LastSeenAtUtc.ShouldBe(PipelineHarness.T0);
    }
}
