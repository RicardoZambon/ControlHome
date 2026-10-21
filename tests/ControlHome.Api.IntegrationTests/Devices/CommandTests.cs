using System.Net;
using System.Text.Json;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Api.IntegrationTests.Devices;

[Collection(DevicesCollection.Name)]
public sealed class CommandTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task A_command_is_accepted_confirmed_audited_and_changes_the_state()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);
        var on = AttributeId(light, CapabilityIds.Switch, "on");
        var target = (await StateOfAsync(member, on)) == StateValue.True ? "turnOff" : "turnOn";

        var commandId = await SendAcceptedAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, target);

        await Factory.Realtime.WaitForCommandAsync(commandId, CommandStatuses.Confirmed);
        Factory.Realtime.CommandStatuses.Where(s => s.CommandId == commandId).Select(s => s.Status)
            .ShouldBe([CommandStatuses.Pending, CommandStatuses.Confirmed]);
        await WaitForStateAsync(member, on, StateValue.Bool(target == "turnOn"));
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (await AuditEntriesAsync("commands.executed")).Count == 1,
            "the command is audited");
        var audit = (await AuditEntriesAsync("commands.executed")).Single();
        (audit.ActorName, audit.TargetType, audit.TargetId).ShouldBe(("member", "endpoint", MainEndpoint(light).Id.ToString()));
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("command").GetString().ShouldBe(target);
        details.RootElement.GetProperty("outcome").GetString().ShouldBe(CommandStatuses.Confirmed);
    }

    [Fact]
    public async Task Arguments_are_validated_against_the_definition_and_the_endpoint_constraints()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);

        using var outOfRange = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Level, "setLevel", """{"level":500}""");
        using var missing = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Level, "setLevel", "{}");
        using var unknown = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Level, "setLevel", """{"level":10,"speed":2}""");

        await outOfRange.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "commands.invalid_arguments");
        (await ErrorsAsync(outOfRange))["arguments.level"].ShouldBe(["commands.argument_out_of_range"]);
        (await ErrorsAsync(missing))["arguments.level"].ShouldBe(["commands.argument_required"]);
        (await ErrorsAsync(unknown))["arguments.speed"].ShouldBe(["commands.argument_unknown"]);
        Factory.Realtime.CommandStatuses.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_endpoints_are_404_and_unsupported_commands_400()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);

        using var noEndpoint = await SendCommandAsync(member, Guid.NewGuid(), CapabilityIds.Switch, "turnOn");
        using var noCapability = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Lock, "unlock");
        using var noCommand = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "explode");
        using var badKey = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "turnOn", idempotencyKey: "not a key!");

        await noEndpoint.ShouldBeProblemAsync(HttpStatusCode.NotFound, "endpoints.not_found");
        await noCapability.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "commands.unsupported");
        await noCommand.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "commands.unsupported");
        await badKey.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_is_409_and_reaches_the_connector_once()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, "spy");
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);
        var key = Guid.NewGuid().ToString("N");

        var commandId = await SendAcceptedAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle", idempotencyKey: key);
        using var again = await SendCommandAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle", idempotencyKey: key);
        using var byAdmin = await SendCommandAsync(admin, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle", idempotencyKey: key);

        await again.ShouldBeProblemAsync(HttpStatusCode.Conflict, "commands.duplicate");
        await byAdmin.ShouldBeProblemAsync(HttpStatusCode.Conflict, "commands.duplicate");
        await Factory.Realtime.WaitForCommandAsync(commandId, CommandStatuses.Confirmed);
        await Task.Delay(300, Ct);
        Factory.Spy.Batches.ShouldHaveSingleItem().IdempotencyKey.ShouldBe(key);
    }

    [Fact]
    public async Task Without_a_key_the_server_generates_one_per_command()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, "spy");
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);

        var first = await SendAcceptedAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle");
        var second = await SendAcceptedAsync(member, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle");

        await Factory.Realtime.WaitForCommandAsync(first, CommandStatuses.Confirmed);
        await Factory.Realtime.WaitForCommandAsync(second, CommandStatuses.Confirmed);
        Factory.Spy.Batches.Select(b => b.IdempotencyKey).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Rapid_setters_are_coalesced_and_the_last_one_wins()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, "spy");
        var light = DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Light);
        var endpoint = MainEndpoint(light).Id;
        var level = AttributeId(light, CapabilityIds.Level, "level");

        var ids = new List<Guid>();
        foreach (var value in new[] { 10, 20, 30, 40 })
        {
            ids.Add(await SendAcceptedAsync(member, endpoint, CapabilityIds.Level, "setLevel", $$"""{"level":{{value}}}"""));
        }

        // The first setter goes out at once, the last one when the 300 ms window ends; those in between are superseded
        // (on a slow machine a window may close between two requests, so only the outline is fixed).
        await Factory.Realtime.WaitForCommandAsync(ids[0], CommandStatuses.Confirmed);
        await Factory.Realtime.WaitForCommandAsync(ids[3], CommandStatuses.Confirmed);
        var finals = ids.Select(id => Factory.Realtime.CommandStatuses.Last(s => s.CommandId == id)).ToList();
        finals.ShouldAllBe(s => s.Status == CommandStatuses.Confirmed || s.Status == CommandStatuses.Superseded);
        finals.Count(s => s.Status == CommandStatuses.Superseded).ShouldBeGreaterThan(0);
        var sent = Factory.Spy.Batches.Select(b => b.Commands[0].Arguments.GetProperty("level").GetInt32()).ToList();
        sent.Count.ShouldBe(finals.Count(s => s.Status == CommandStatuses.Confirmed));
        (sent[0], sent[^1]).ShouldBe((10, 40));
        await WaitForStateAsync(member, level, StateValue.Number(40));
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        var json = await response.BodyAsync();
        return json.GetProperty("errors").EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray(),
            StringComparer.Ordinal);
    }
}
