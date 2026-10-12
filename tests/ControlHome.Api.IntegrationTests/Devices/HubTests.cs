using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Auth;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

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
