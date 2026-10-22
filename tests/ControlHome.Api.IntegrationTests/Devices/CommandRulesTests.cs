using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// The command service's rules that depend on the device and its integration: gate attributes, safety by device class
/// (from the database), and integrations that are not running.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class CommandRulesTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task A_gated_command_needs_its_gate_attribute_true_and_never_reaches_the_connector_otherwise()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, GadgetsConnectorFactory.Key, "{}");
        var washer = DeviceOfClass(await WaitForDevicesAsync(member, 3), DeviceClasses.Washer);
        var endpoint = MainEndpoint(washer).Id;

        using (var closed = await SendCommandAsync(member, endpoint, CapabilityIds.ApplianceCycle, "start"))
        {
            await closed.ShouldBeProblemAsync(HttpStatusCode.Conflict, "commands.gate_closed");
        }

        Factory.Spy.Batches.ShouldBeEmpty();
        var remote = AttributeId(washer, CapabilityIds.ApplianceCycle, "remoteControlEnabled");
        using (var inject = await admin.PostAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/_test/devices/{remote}/state"),
            new { value = StateValue.True, observedAtUtc = DateTimeOffset.UtcNow.AddDays(30) },
            Ct))
        {
            await inject.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        var started = await SendAcceptedAsync(member, endpoint, CapabilityIds.ApplianceCycle, "start");
        await Factory.Realtime.WaitForCommandAsync(started, CommandStatuses.Confirmed);
        Factory.Spy.Batches.ShouldHaveSingleItem().Commands[0].Command.ShouldBe("start");
    }

    [Fact]
    public async Task Opening_a_gate_needs_a_step_up_but_opening_a_blind_does_not()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, GadgetsConnectorFactory.Key, "{}");
        var devices = await WaitForDevicesAsync(member, 3);
        var gate = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Gate)).Id;
        var blind = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Blind)).Id;

        await SendAcceptedAsync(member, blind, CapabilityIds.Cover, "open");
        var blindPosition = await SendAcceptedAsync(member, blind, CapabilityIds.Cover, "setPosition", """{"position":40}""");
        foreach (var command in new[] { "open", "setPosition" })
        {
            using var refused = await SendCommandAsync(member, gate, CapabilityIds.Cover, command, command == "open" ? null : """{"position":40}""");
            await refused.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "safety.step_up_required");
        }

        await SetPinAndStepUpAsync(member);
        var gatePosition = await SendAcceptedAsync(member, gate, CapabilityIds.Cover, "setPosition", """{"position":40}""");

        await Factory.Realtime.WaitForCommandAsync(gatePosition, CommandStatuses.Confirmed);
        await Factory.Realtime.WaitForCommandAsync(blindPosition, CommandStatuses.Confirmed);
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (await AuditEntriesAsync("commands.executed")).Count == 3,
            "every command is audited");
        var audits = await AuditEntriesAsync("commands.executed");

        // Safety-critical on a gate: audited without its arguments; on a blind it is an ordinary setter.
        audits.Single(a => a.DetailsJson!.Contains(gatePosition.ToString(), StringComparison.Ordinal)).DetailsJson!.ShouldNotContain("\"arguments\":{");
        audits.Single(a => a.DetailsJson!.Contains(blindPosition.ToString(), StringComparison.Ordinal)).DetailsJson!.ShouldContain("\"position\":40");
    }

    [Fact]
    public async Task A_command_for_an_integration_that_is_not_running_is_503()
    {
        var (admin, member, _) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin);
        var light = MainEndpoint(DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light)).Id;

        await Factory.Services.GetRequiredService<IIntegrationRuntimeControl>().StopAsync(integration.Id, Ct);

        using var response = await SendCommandAsync(member, light, CapabilityIds.Switch, "toggle");
        await response.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "commands.unavailable");
        Factory.Realtime.CommandStatuses.ShouldBeEmpty();
    }
}
