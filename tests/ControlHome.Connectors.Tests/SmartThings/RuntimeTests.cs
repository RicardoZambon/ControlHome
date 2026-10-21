using System.Net;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>The SmartThings runtime against a fake SmartThings cloud (WireMock) and a fake clock.</summary>
public sealed class RuntimeTests : IDisposable
{
    private const string Location = FakeSmartThings.LocationId;

    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(30);

    private readonly FakeSmartThings _fake = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task The_bulk_poll_syncs_the_inventory_with_rooms_and_reports_states()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Light, Location), StFixtures.ListingItem(StFixtures.Plug, Location), StFixtures.ListingItem(StFixtures.Tv, Location));
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 1, "the inventory is synced");
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.Ok), "the status is Ok");

        st.Context.Syncs[0].ShouldBe([StFixtures.DeviceId(StFixtures.Light), StFixtures.DeviceId(StFixtures.Plug), StFixtures.DeviceId(StFixtures.Tv)], ignoreOrder: true);
        st.Context.Devices.Single(d => d.ExternalId == StFixtures.DeviceId(StFixtures.Plug)).SuggestedRoom.ShouldBe("Theater");
        st.Context.Latest(StFixtures.DeviceId(StFixtures.Plug), CapabilityIds.Switch, "on")!.Value.ShouldBe(StateValue.True);
        st.Context.Latest(StFixtures.DeviceId(StFixtures.Tv), CapabilityIds.MediaPlayer, "volume")!.Value.ShouldBe(StateValue.Number(13));
        st.Context.Availability.ShouldContain((StFixtures.DeviceId(StFixtures.Light), Availability.Online));

        var list = _fake.Requests("/v1/devices", "GET").ShouldHaveSingleItem().RequestMessage!;
        list.Url.ShouldContain("locationId=" + Location);
        list.Url.ShouldContain("includeStatus=true");
        list.Url.ShouldContain("includeHealth=true");
        list.Url.ShouldContain("includeRestricted=true");
        list.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Bearer access-token-0001");
        _fake.Requests($"/v1/locations/{Location}/rooms").Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unchanged_inventory_is_not_saved_again_and_only_changed_values_are_reported()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Light, Location));
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");
        var reported = st.Context.States.Count;

        await NextPollAsync(st);

        st.Context.Syncs.Count.ShouldBe(1);
        st.Context.States.Count.ShouldBe(reported);
        _fake.Requests($"/v1/locations/{Location}/rooms").Count.ShouldBe(1); // cached
    }

    [Fact]
    public async Task A_device_gone_from_the_listing_is_orphaned_and_an_empty_location_still_syncs()
    {
        Rooms();
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).InScenario("inventory").WillSetStateTo("one")
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Light, Location), StFixtures.ListingItem(StFixtures.Plug, Location)])));
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).InScenario("inventory").WhenStateIs("one").WillSetStateTo("none")
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Light, Location)])));
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).InScenario("inventory").WhenStateIs("none")
            .RespondWith(Json(StFixtures.Listing([])));
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");

        await NextPollAsync(st);
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 2, "the second inventory");
        st.Context.Syncs[1].ShouldBe([StFixtures.DeviceId(StFixtures.Light)]);

        await NextPollAsync(st);
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 3, "the empty inventory");
        st.Context.Syncs[2].ShouldBeEmpty();
    }

    [Fact]
    public async Task The_first_listing_of_an_empty_location_is_saved_so_devices_that_left_are_orphaned()
    {
        Rooms();
        Listing();
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 1, "the inventory is synced");

        st.Context.Syncs[0].ShouldBeEmpty();
    }

    [Fact]
    public async Task Pages_are_followed_and_a_link_to_another_host_is_never_followed()
    {
        Rooms();
        var next = _fake.Url + $"/v1/devices?locationId={Location}&page=2";
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").WithParam("page", "2").UsingGet()).AtPriority(1)
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Plug, Location)])));
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).AtPriority(5)
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Light, Location)], next)));
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 1, "the inventory is synced");

        st.Context.Syncs[0].Count.ShouldBe(2);
        _fake.Requests("/v1/devices").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_listing_that_points_elsewhere_is_partial_and_never_orphans_devices()
    {
        Rooms();
        Listing([StFixtures.ListingItem(StFixtures.Light, Location)], next: "https://elsewhere.example.test/v1/devices?page=2");
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Devices.Count == 1, "the device is upserted");

        st.Context.Syncs.ShouldBeEmpty();
        _fake.Requests("/v1/devices").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_poll_waits_for_the_window_reset_when_the_rate_limit_headers_say_it_is_used_up()
    {
        Rooms();
        // Remaining 0, reset in 45 000 — milliseconds (above 1000), so 45 s.
        Listing([StFixtures.ListingItem(StFixtures.Light, Location)], headers: new() { ["X-RateLimit-Limit"] = "12", ["X-RateLimit-Remaining"] = "0", ["X-RateLimit-Reset"] = "45000" });
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");

        // 30 s later the budget is still used up: no call (it would only earn a 429).
        await AdvanceAsync(st, _poll);
        _fake.Requests("/v1/devices").Count.ShouldBe(1);

        // After the reset the poll goes out.
        await AdvanceAsync(st, _poll);
        await st.WaitUntilAsync(() => _fake.Requests("/v1/devices").Count == 2, "the next poll after the reset");
    }

    [Fact]
    public async Task A_429_blocks_polling_until_retry_after_then_recovers()
    {
        Rooms();
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).InScenario("limit").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "120").WithBody("""{"error":{"code":"TooManyRequestError"}}"""));
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).InScenario("limit").WhenStateIs("ok")
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Light, Location)])));
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.Degraded), "rate limited");
        st.Context.Statuses.Last().Message!.ShouldContain("rate limit");

        await AdvanceAsync(st, TimeSpan.FromSeconds(60));
        _fake.Requests("/v1/devices").Count.ShouldBe(1);

        await AdvanceAsync(st, TimeSpan.FromSeconds(61));
        await st.WaitUntilAsync(() => st.Context.Statuses.Last().Status == IntegrationStatus.Ok, "recovered");
        _fake.Requests("/v1/devices").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_plan_error_sets_plan_required_and_refuses_commands()
    {
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json")
                .WithBody("""{"requestId":"r-1","error":{"code":"Forbidden","message":"API access requires a SmartThings Personal Plan subscription."}}"""));
        Rooms();
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.PlanRequired), "plan required");

        st.Context.Statuses.Last().Message!.ShouldContain("Personal Plan");
        var outcome = await st.Runtime.ExecuteAsync(StFixtures.Batch("any", "k-1", StFixtures.Command("main", CapabilityIds.Switch, "turnOn")), Ct);
        outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Unavailable);
    }

    [Fact]
    public async Task A_rejected_access_token_is_refreshed_once_and_the_read_repeated()
    {
        Rooms();
        TokenEndpoint();
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").WithHeader("Authorization", "Bearer access-token-0002").UsingGet()).AtPriority(1)
            .RespondWith(Json(StFixtures.Listing([StFixtures.ListingItem(StFixtures.Light, Location)])));
        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("""{"error":{"code":"Unauthorized"}}"""));
        var log = new LogCapture();
        await using var st = Harness(logger: log);

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Syncs.Count == 1, "the inventory after the refresh");

        _fake.Requests("/oauth/token", "POST").Count.ShouldBe(1);
        _fake.Requests("/v1/devices").Count.ShouldBe(2);
        TokenSet.Deserialize(st.Secrets.Values[SecretKeys.Tokens])!.RefreshToken.ShouldBe("refresh-token-0002");
        foreach (var secret in (string[])["access-token-0001", "access-token-0002", "refresh-token-0001", "refresh-token-0002", FakeSmartThings.ClientSecret])
        {
            log.Messages.ShouldAllBe(m => !m.Contains(secret, StringComparison.Ordinal));
            st.Context.Statuses.ShouldAllBe(s => s.Message == null || !s.Message.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_spent_refresh_token_means_reauthorize()
    {
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("""{"error":"invalid_grant"}"""));
        await using var st = Harness(StHarness.OAuthSecrets(validFor: TimeSpan.FromHours(1)));

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.NeedsReauth), "needs reauth");

        st.Context.Statuses.Last().Message!.ShouldContain("Sign in again");
        _fake.Requests("/v1/devices").ShouldBeEmpty();
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_tokens_ask_for_reauthorization_without_calling_smartthings()
    {
        await using var st = Harness(new MemorySecretStore());

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.NeedsReauth), "needs reauth");

        _fake.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_is_sent_once_with_the_component_and_confirmed_by_hot_polls()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Light, Location));
        var light = StFixtures.DeviceId(StFixtures.Light);
        Commands(light, "ACCEPTED");
        var on = StFixtures.Text("device_status", StFixtures.Light + ".json").Replace("\"value\": \"off\"", "\"value\": \"on\"", StringComparison.Ordinal);
        _fake.Server.Given(Request.Create().WithPath($"/v1/devices/{light}/status").UsingGet()).RespondWith(Json(on));
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");

        var outcome = await st.Runtime.ExecuteAsync(StFixtures.Batch(light, "k-1", StFixtures.Command("main", CapabilityIds.Switch, "turnOn")), Ct);

        outcome.ShouldBeOfType<CommandOutcome.Accepted>();
        await Task.Delay(200, Ct); // the command woke the loop: let it settle
        var post = _fake.Requests($"/v1/devices/{light}/commands", "POST").ShouldHaveSingleItem().RequestMessage!;
        JsonNode.Parse(post.Body!)!.ToJsonString().ShouldBe("""{"commands":[{"component":"main","capability":"switch","command":"on","arguments":[]}]}""");

        // ACCEPTED is only a receipt: the device is polled every 5 s for 30 s, and the state confirms the command.
        await AdvanceAsync(st, TimeSpan.FromSeconds(5));
        await st.WaitUntilAsync(() => st.Context.Latest(light, CapabilityIds.Switch, "on")?.Value == StateValue.True, "the switch is on");
        for (var i = 0; i < 8; i++)
        {
            await AdvanceAsync(st, TimeSpan.FromSeconds(5));
        }

        var polls = _fake.Requests($"/v1/devices/{light}/status").Count;
        polls.ShouldBeInRange(5, 6);

        // Duplicate key: not sent again.
        var again = await st.Runtime.ExecuteAsync(StFixtures.Batch(light, "k-1", StFixtures.Command("main", CapabilityIds.Switch, "turnOn")), Ct);
        again.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Duplicate);
        _fake.Requests($"/v1/devices/{light}/commands").Count.ShouldBe(1);
    }

    [Fact]
    public async Task More_than_ten_commands_go_out_in_requests_of_ten()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Light, Location));
        var light = StFixtures.DeviceId(StFixtures.Light);
        Commands(light, "ACCEPTED");
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");
        var commands = Enumerable.Range(0, 12).Select(i => StFixtures.Command("main", CapabilityIds.Level, "setLevel", $$"""{"level":{{i + 1}}}""")).ToArray();

        var outcome = await st.Runtime.ExecuteAsync(StFixtures.Batch(light, "k-2", commands), Ct);

        outcome.ShouldBeOfType<CommandOutcome.Accepted>();
        var bodies = _fake.Requests($"/v1/devices/{light}/commands").Select(r => JsonNode.Parse(r.RequestMessage!.Body!)!["commands"]!.AsArray().Count).ToList();
        bodies.ShouldBe([10, 2]);
    }

    [Fact]
    public async Task A_failed_command_is_refused_and_an_unlock_needs_an_idempotency_key()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Lock, Location));
        var doorLock = StFixtures.DeviceId(StFixtures.Lock);
        Commands(doorLock, "FAILED");
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");

        var noKey = await st.Runtime.ExecuteAsync(StFixtures.Batch(doorLock, "", StFixtures.Command("main", CapabilityIds.Lock, "unlock")), Ct);
        noKey.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        _fake.Requests($"/v1/devices/{doorLock}/commands").ShouldBeEmpty();

        var failed = await st.Runtime.ExecuteAsync(StFixtures.Batch(doorLock, "k-3", StFixtures.Command("main", CapabilityIds.Lock, "lock")), Ct);
        failed.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Refused);
    }

    [Fact]
    public async Task An_unlock_that_times_out_is_never_sent_twice()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Lock, Location));
        var doorLock = StFixtures.DeviceId(StFixtures.Lock);
        _fake.Server.Given(Request.Create().WithPath($"/v1/devices/{doorLock}/commands").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)).WithBody("""{"results":[{"id":"c-1","status":"ACCEPTED"}]}"""));
        await using var st = Harness();
        st.Start();
        await st.WaitUntilAsync(() => st.Runtime.Polls == 1, "the first poll");

        var command = st.Runtime.ExecuteAsync(StFixtures.Batch(doorLock, "k-4", StFixtures.Command("main", CapabilityIds.Lock, "unlock")), Ct);
        await st.WaitUntilAsync(() => _fake.Server.LogEntries.Count(e => e.RequestMessage?.Method == "POST") == 0 && !command.IsCompleted, "the request is in flight");
        await Task.Delay(300, Ct);
        st.Advance(st.Options.RequestTimeout + TimeSpan.FromSeconds(1));

        var outcome = await command;
        outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Unavailable);
        await Task.Delay(2500, Ct);
        _fake.Requests($"/v1/devices/{doorLock}/commands").Count.ShouldBeLessThanOrEqualTo(1);

        // The same key again is a duplicate: at most once, even after a timeout.
        var again = await st.Runtime.ExecuteAsync(StFixtures.Batch(doorLock, "k-4", StFixtures.Command("main", CapabilityIds.Lock, "unlock")), Ct);
        again.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Duplicate);
    }

    [Fact]
    public async Task Development_mode_warns_that_the_token_expires()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Plug, Location));
        await using var st = Harness(new MemorySecretStore(new Dictionary<string, string> { [SecretKeys.PersonalAccessToken] = "pat-0001" }), StHarness.PatConfig());

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Statuses.Any(s => s.Status == IntegrationStatus.Ok), "the status is Ok");

        var message = st.Context.Statuses.Last(s => s.Status == IntegrationStatus.Ok).Message!;
        message.ShouldContain("Development mode");
        message.ShouldContain("2026-10-22 08:00 UTC");
        _fake.Requests("/v1/devices").ShouldHaveSingleItem().RequestMessage!.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Bearer pat-0001");
    }

    [Fact]
    public async Task The_status_endpoint_gives_the_hot_poll_a_fresh_state_and_offline_devices_are_reported()
    {
        Rooms();
        Listing(StFixtures.ListingItem(StFixtures.Plug, Location, health: "OFFLINE"));
        await using var st = Harness();

        st.Start();
        await st.WaitUntilAsync(() => st.Context.Availability.Contains((StFixtures.DeviceId(StFixtures.Plug), Availability.Offline)), "the plug is offline");
    }

    private StHarness Harness(MemorySecretStore? secrets = null, string? config = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(_fake, secrets ?? StHarness.OAuthSecrets(validFor: TimeSpan.FromHours(20)), config ?? StHarness.OAuthConfig(), logger: logger);

    /// <summary>
    /// Lets the loop settle into its wait, moves the clock and lets the loop run what became due (if anything did) until it
    /// waits again.
    /// </summary>
    private static async Task AdvanceAsync(StHarness st, TimeSpan by)
    {
        await Task.Delay(150, Ct);
        var iterations = st.Runtime.Iterations;
        st.Advance(by);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (st.Runtime.Iterations == iterations && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(20, Ct);
        }

        await Task.Delay(100, Ct);
    }

    private static async Task NextPollAsync(StHarness st)
    {
        var polls = st.Runtime.Polls;
        await AdvanceAsync(st, _poll);
        await st.WaitUntilAsync(() => st.Runtime.Polls > polls, "the next poll");
        await Task.Delay(100, Ct);
    }

    private void Rooms() =>
        _fake.Server.Given(Request.Create().WithPath($"/v1/locations/{Location}/rooms").UsingGet()).RespondWith(Json(StFixtures.Text("rooms.json")));

    private void Listing(params JsonObject[] items) => Listing(items, next: null);

    private void Listing(JsonObject[] items, string? next = null, Dictionary<string, string>? headers = null)
    {
        var response = Json(StFixtures.Listing(items, next));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(name, value);
        }

        _fake.Server.Given(Request.Create().WithPath("/v1/devices").UsingGet()).AtPriority(10).RespondWith(response);
    }

    private void Commands(string deviceId, string status) =>
        _fake.Server.Given(Request.Create().WithPath($"/v1/devices/{deviceId}/commands").UsingPost())
            .RespondWith(Json($$"""{"results":[{"id":"00000000-0000-4000-8000-0000000c0001","status":"{{status}}"}]}"""));

    private void TokenEndpoint() =>
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost()).RespondWith(Json(new JsonObject
        {
            ["access_token"] = "access-token-0002",
            ["refresh_token"] = "refresh-token-0002",
            ["expires_in"] = 86399,
        }.ToJsonString()));

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithStatusCode(HttpStatusCode.OK).WithHeader("Content-Type", "application/json").WithBody(body);
}
