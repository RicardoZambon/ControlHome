using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Realtime;
using ControlHome.Application.Auth;
using ControlHome.Application.Realtime;
using ControlHome.Application.Users;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>The live-update hub through the request edge: WebSocket from the app's own origin, session cookie.</summary>
[Collection(DevicesCollection.Name)]
public sealed class HubTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    private const string SameOrigin = "http://localhost";

    [Fact]
    public async Task A_signed_in_client_receives_state_changes_and_command_status()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var level = AttributeId(light, CapabilityIds.Level, "level");
        await using var hub = await ConnectAsync(await SessionCookieAsync("viewer"), SameOrigin);
        var deltas = new ConcurrentQueue<StateDelta>();
        var statuses = new ConcurrentQueue<CommandStatusDto>();
        hub.On<StateDelta[]>(HomeHubMessages.StateChanged, batch => batch.ToList().ForEach(deltas.Enqueue));
        hub.On<CommandStatusDto>(HomeHubMessages.CommandStatus, statuses.Enqueue);
        await hub.StartAsync(Ct);

        using (var inject = await admin.PostAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/_test/devices/{level}/state"),
            new { value = StateValue.Number(63), observedAtUtc = DateTimeOffset.UtcNow.AddDays(30) },
            Ct))
        {
            await inject.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await RecordingRealtimePublisher.WaitUntilAsync(
            () => deltas.Any(d => d.AttributeId == level && d.Value == StateValue.Number(63)),
            "the hub client received the delta");

        var commandId = await SendAcceptedAsync(admin, MainEndpoint(light).Id, CapabilityIds.Switch, "toggle");
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => statuses.Any(s => s.CommandId == commandId && s.Status == CommandStatuses.Confirmed),
            "the hub client received the command status");
        statuses.Where(s => s.CommandId == commandId).Select(s => s.Status).First().ShouldBe(CommandStatuses.Pending);
    }

    [Fact]
    public async Task Disabling_a_user_closes_their_open_hub_connection_at_once()
    {
        var (admin, _, _) = await SignInAllAsync();
        var viewerId = await UserIdAsync(admin, "viewer");
        await using var hub = await ConnectAsync(await SessionCookieAsync("viewer"), SameOrigin);
        var closed = ClosedSignal(hub);
        await hub.StartAsync(Ct);
        await RecordingRealtimePublisher.WaitUntilAsync(() => Registry.CountFor(viewerId) == 1, "the connection is registered");

        using (var disable = await admin.PutAsJsonAsync($"/api/users/{viewerId}", new UpdateUserRequest("viewer display", Roles.Viewer, IsDisabled: true), Ct))
        {
            await disable.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        hub.State.ShouldBe(HubConnectionState.Disconnected);
        Registry.CountFor(viewerId).ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_or_demoting_a_user_closes_their_open_hub_connections()
    {
        var (admin, _, _) = await SignInAllAsync();
        var memberId = await UserIdAsync(admin, "member");
        var viewerId = await UserIdAsync(admin, "viewer");
        await using var member = await ConnectAsync(await SessionCookieAsync("member"), SameOrigin);
        await using var viewer = await ConnectAsync(await SessionCookieAsync("viewer"), SameOrigin);
        var memberClosed = ClosedSignal(member);
        var viewerClosed = ClosedSignal(viewer);
        await member.StartAsync(Ct);
        await viewer.StartAsync(Ct);
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Registry.CountFor(memberId) == 1 && Registry.CountFor(viewerId) == 1,
            "both connections are registered");

        using (var demote = await admin.PutAsJsonAsync($"/api/users/{memberId}", new UpdateUserRequest("member display", Roles.Viewer, IsDisabled: false), Ct))
        {
            await demote.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        using (var delete = await admin.DeleteAsync($"/api/users/{viewerId}", Ct))
        {
            await delete.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await memberClosed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await viewerClosed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_connection_opened_with_a_session_revoked_moments_before_is_closed()
    {
        var (admin, _, _) = await SignInAllAsync();
        var viewerId = await UserIdAsync(admin, "viewer");
        var cookie = await SessionCookieAsync("viewer");

        // The cookie is still accepted for up to a minute (the stamp is re-checked per interval), but its stamp is stale.
        using (var reset = await admin.PostAsJsonAsync($"/api/users/{viewerId}/password", new ResetPasswordRequest("another long password"), Ct))
        {
            await reset.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await using var hub = await ConnectAsync(cookie, SameOrigin);
        var closed = ClosedSignal(hub);
        try
        {
            await hub.StartAsync(Ct);
        }
        catch (Exception)
        {
            // Closed during the handshake: just as good.
            return;
        }

        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Registry.CountFor(viewerId).ShouldBe(0);
    }

    [Fact]
    public async Task The_periodic_check_closes_connections_whose_session_changed_by_any_path()
    {
        var (admin, _, _) = await SignInAllAsync();
        var viewerId = await UserIdAsync(admin, "viewer");
        await using var hub = await ConnectAsync(await SessionCookieAsync("viewer"), SameOrigin);
        var closed = ClosedSignal(hub);
        await hub.StartAsync(Ct);
        await RecordingRealtimePublisher.WaitUntilAsync(() => Registry.CountFor(viewerId) == 1, "the connection is registered");

        // Changed behind the services' back (no revocation call): only the backstop can notice.
        await UpdateDbAsync(async db =>
        {
            var viewer = await db.Users.SingleAsync(u => u.Id == viewerId, Ct);
            viewer.SecurityStamp = Guid.NewGuid().ToString("N");
        });
        await Factory.Services.GetServices<IHostedService>().OfType<HubSessionValidator>().Single().ValidateAsync(Ct);

        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task The_hub_refuses_a_websocket_from_another_origin()
    {
        await SignInAllAsync();
        await using var hub = await ConnectAsync(await SessionCookieAsync("viewer"), "http://evil.example");

        var error = await Should.ThrowAsync<Exception>(() => hub.StartAsync(Ct));

        error.ToString().ShouldContain("403");
    }

    [Fact]
    public async Task The_hub_refuses_anonymous_clients()
    {
        await SignInAllAsync();
        await using var hub = await ConnectAsync(cookie: null, SameOrigin);

        var error = await Should.ThrowAsync<Exception>(() => hub.StartAsync(Ct));

        error.ToString().ShouldContain("401");
    }

    [Fact]
    public async Task Negotiating_needs_the_same_origin_and_the_marker_header()
    {
        await SignInAllAsync();
        var cookie = await SessionCookieAsync("viewer");
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var withoutHeader = await NegotiateAsync(client, cookie, SameOrigin, marker: false);
        using var foreign = await NegotiateAsync(client, cookie, "http://evil.example", marker: true);
        using var ok = await NegotiateAsync(client, cookie, SameOrigin, marker: true);

        await withoutHeader.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "request.missing_header");
        await foreign.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "request.cross_origin");
        await ok.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync(Ct)).ShouldContain("WebSockets");
    }

    private HubConnectionRegistry Registry => Factory.Services.GetRequiredService<HubConnectionRegistry>();

    /// <summary>Completes when the client's connection closes (from the server's side: no automatic reconnect here).</summary>
    private static Task ClosedSignal(HubConnection hub)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client, string cookie, string origin, bool marker)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{HomeHubMessages.Path}/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", origin);
        if (marker)
        {
            request.Headers.Add("X-ControlHome", "1");
        }

        return client.SendAsync(request, Ct);
    }

    /// <summary>A hub connection over the test server's WebSocket client (no negotiate), sending the given Origin and cookie.</summary>
    private Task<HubConnection> ConnectAsync(string? cookie, string origin)
    {
        var server = Factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HomeHubMessages.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        request.Headers.Origin = origin;
                        if (cookie is not null)
                        {
                            request.Headers.Cookie = cookie;
                        }
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        return Task.FromResult(connection);
    }

    /// <summary>Signs in and returns the session cookie (<c>name=value</c>).</summary>
    private async Task<string> SessionCookieAsync(string userName)
    {
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(userName, Password, RememberMe: false)),
        };
        request.Headers.Add("Origin", SameOrigin);
        request.Headers.Add("X-ControlHome", "1");
        using var response = await client.SendAsync(request, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ControlHome.Session=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }
}
