using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Device-test helpers on top of <see cref="ResetAppTestBase{TFactory}"/>; every test starts from an empty home.</summary>
public abstract class DevicesTestBase(DevicesApiFactory factory) : ResetAppTestBase<DevicesApiFactory>(factory)
{
    protected const string Password = "a long enough password";
    protected const string Pin = "4321";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Factory.Realtime.Clear();
        Factory.Spy.Clear();
        Factory.Logs.Clear();
    }

    /// <summary>Completes setup and returns admin, member and viewer clients.</summary>
    protected async Task<(HttpClient Admin, HttpClient Member, HttpClient Viewer)> SignInAllAsync()
    {
        var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        await admin.CreateUserAsync("viewer", Password, Roles.Viewer, Ct);
        return (admin, await Factory.LoginAsync("member", Password, Ct), await Factory.LoginAsync("viewer", Password, Ct));
    }

    protected static async Task<IntegrationDto> CreateIntegrationAsync(HttpClient admin, string connectorKey = "simulated", string config = """{"preset":"basic"}""")
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/integrations",
            new CreateIntegrationRequest(connectorKey, $"Test {connectorKey}", Json.Of(config)),
            Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IntegrationDto>(Ct))!;
    }

    /// <summary>Waits until <paramref name="count"/> devices are listed, each with its endpoints' attributes reported.</summary>
    protected static async Task<IReadOnlyList<DeviceSummaryDto>> WaitForDevicesAsync(HttpClient client, int count)
    {
        IReadOnlyList<DeviceSummaryDto> devices = [];
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () =>
            {
                devices = (await client.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Ct))!;
                return devices.Count == count
                    && devices.All(d => d.Availability == "Online"
                        && d.Endpoints.SelectMany(e => e.Capabilities).SelectMany(c => c.Attributes).Any(a => a.Value.Kind != StateValueKind.Null));
            },
            $"{count} devices are online with values");
        return devices;
    }

    protected static DeviceSummaryDto DeviceOfClass(IReadOnlyList<DeviceSummaryDto> devices, string deviceClass) =>
        devices.Single(d => d.DeviceClass == deviceClass);

    protected static EndpointDto MainEndpoint(DeviceSummaryDto device) => device.Endpoints.Single(e => e.Key == "main");

    protected static int AttributeId(DeviceSummaryDto device, string capability, string key) =>
        MainEndpoint(device).Capabilities.Single(c => c.CapabilityId == capability).Attributes.Single(a => a.Key == key).AttributeId;

    protected static Task<HttpResponseMessage> SendCommandAsync(
        HttpClient client,
        Guid endpointId,
        string capability,
        string command,
        string? arguments = null,
        string? idempotencyKey = null) =>
        client.PostAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/endpoints/{endpointId}/commands"),
            new CommandRequest(capability, command, arguments is null ? null : Json.Of(arguments), idempotencyKey),
            Ct);

    /// <summary>Sends a command that must be accepted (202) and returns its id.</summary>
    protected static async Task<Guid> SendAcceptedAsync(
        HttpClient client,
        Guid endpointId,
        string capability,
        string command,
        string? arguments = null,
        string? idempotencyKey = null)
    {
        using var response = await SendCommandAsync(client, endpointId, capability, command, arguments, idempotencyKey);
        await response.ShouldHaveStatusAsync(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<CommandAcceptedDto>(Ct))!.CommandId;
    }

    /// <summary>The current value of an attribute from <c>GET /api/state</c>.</summary>
    protected static async Task<StateValue?> StateOfAsync(HttpClient client, int attributeId)
    {
        var states = await client.GetFromJsonAsync<AttributeStateDto[]>("/api/state", Ct);
        return states!.SingleOrDefault(s => s.AttributeId == attributeId)?.Value;
    }

    protected static async Task WaitForStateAsync(HttpClient client, int attributeId, StateValue expected) =>
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => Equals(await StateOfAsync(client, attributeId), expected),
            $"attribute {attributeId} is {JsonSerializer.Serialize(expected)}");

    protected static async Task SetPinAndStepUpAsync(HttpClient client, string pin = Pin)
    {
        using (var set = await client.PutAsJsonAsync("/api/auth/me/pin", new { newPin = pin }, Ct))
        {
            await set.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await StepUpAsync(client, pin, HttpStatusCode.NoContent);
    }

    protected static async Task<HttpResponseMessage> StepUpAsync(HttpClient client, string pin, HttpStatusCode? expected = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/step-up", new { pin }, Ct);
        if (expected is { } status)
        {
            await response.ShouldHaveStatusAsync(status);
        }

        return response;
    }
}

internal static class ResponseAssertions
{
    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        // Buffered, so the body can be read again afterwards.
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(expected, body);
        }
    }

    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await response.ShouldHaveStatusAsync(status);
        (await response.BodyAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>The (buffered) body as JSON; may be read any number of times.</summary>
    public static async Task<JsonElement> BodyAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
