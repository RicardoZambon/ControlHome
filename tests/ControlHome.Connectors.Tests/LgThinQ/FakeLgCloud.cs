using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.LgThinQ;
using ControlHome.Connectors.LgThinQ.Api;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>
/// LG's ThinQ Connect REST API on WireMock: devices, profiles, state, control, energy, MQTT client registration (a test CA
/// signs the CSRs), route, subscriptions. Answers are wrapped like LG's (<c>{messageId, timestamp, response}</c>).
/// </summary>
internal sealed class FakeLgCloud : IDisposable
{
    public const string Topic = "app/clients/controlhome-test/push";

    private readonly bool _ownsCa;
    private int _certificates;

    /// <param name="ca">The CA that signs client certificates (a test broker's); a new one when <c>null</c>.</param>
    public FakeLgCloud(TestCertificateAuthority? ca = null)
    {
        Server = WireMockServer.Start();
        _ownsCa = ca is null;
        Ca = ca ?? new TestCertificateAuthority();
    }

    public WireMockServer Server { get; }

    public TestCertificateAuthority Ca { get; }

    public Uri BaseUrl => new(Server.Url! + "/");

    /// <summary>Client certificates issued so far.</summary>
    public int Certificates => Volatile.Read(ref _certificates);

    /// <summary>Runtime options pointing at this fake (fast retries, a generous budget).</summary>
    public LgRuntimeOptions Options(LgRuntimeOptions? baseline = null) => (baseline ?? LgRuntimeOptions.Default) with
    {
        BaseUrlOverride = BaseUrl,
        TrustedRootsOverride = [Ca.Certificate],
        Rate = new LgRateOptions(10_000, TimeSpan.FromMilliseconds(1), TimeSpan.FromHours(1)),
        Http = new LgHttpOptions(TimeSpan.FromSeconds(15), 2, TimeSpan.Zero),
        Jitter = 0,
    };

    public FakeLgCloud Ok(string method, string path, string response, int priority = 10)
    {
        Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(LgFixtures.Ok(response)));
        return this;
    }

    public FakeLgCloud Fails(string method, string path, string code, int status = 400, int priority = 1)
    {
        Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(LgFixtures.Error(code)));
        return this;
    }

    /// <summary>Everything a healthy account answers: the fixture devices, profiles, states, subscriptions, client, route.</summary>
    public FakeLgCloud Account(string brokerHost = "broker.test", int brokerPort = 8883, params (string Id, string Fixture)[] devices)
    {
        var list = JsonNode.Parse(LgFixtures.Text("devices.json"))!.AsArray();
        var wanted = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var kept = new JsonArray([.. list.Where(d => wanted.Contains(d!["deviceId"]!.GetValue<string>())).Select(d => d!.DeepClone())]);
        Ok("GET", "/devices", kept.ToJsonString());
        foreach (var (id, fixture) in devices)
        {
            Ok("GET", $"/devices/{id}/profile", LgFixtures.Text($"{fixture}.profile.json"));
            Ok("GET", $"/devices/{id}/state", LgFixtures.Text($"{fixture}.status.json"));
            Ok("GET", $"/devices/energy/{id}/profile", LgFixtures.Text("ac.energy-profile.json"));
            Ok("POST", $"/push/{id}/subscribe", "{}");
            Ok("POST", $"/event/{id}/subscribe", "{}");
            Ok("POST", $"/devices/{id}/control", "{}");
        }

        Ok("POST", "/push/devices", "{}");
        Ok("POST", "/client", "{}");
        Ok("GET", "/route", $$"""{"apiServer":"https://api-aic.lgthinq.com","mqttServer":"mqtts://{{brokerHost}}:{{brokerPort}}"}""");
        Server.Given(Request.Create().WithPath("/client/certificate").UsingPost()).AtPriority(10)
            .RespondWith(Response.Create().WithCallback(request =>
            {
                Interlocked.Increment(ref _certificates);
                var csr = JsonNode.Parse(request.Body!)!["csr"]!.GetValue<string>();
                var answer = new JsonObject
                {
                    ["result"] = new JsonObject
                    {
                        ["certificatePem"] = Ca.SignCsr(csr, TimeSpan.FromDays(365)),
                        ["subscriptions"] = new JsonArray(Topic),
                    },
                };
                return Json(200, LgFixtures.Ok(answer.ToJsonString()));
            }));
        return this;
    }

    /// <summary>Requests received, oldest first (optionally only those on <paramref name="path"/> with <paramref name="method"/>).</summary>
    public IReadOnlyList<IRequestMessage> Requests(string? method = null, string? path = null) =>
        [.. Server.LogEntries
            .Select(e => e.RequestMessage)
            .Where(r => r is not null && (method is null || r.Method == method) && (path is null || r.Path == path))
            .OrderBy(r => r!.DateTime)
            .Cast<IRequestMessage>()];

    public static string Header(IRequestMessage request, string name) =>
        request.Headers!.First(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value.Single();

    public static bool HasHeader(IRequestMessage request, string name) =>
        request.Headers!.Any(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    public static JsonElement Body(IRequestMessage request) => LgFixtures.Parse(request.Body!);

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
        if (_ownsCa)
        {
            Ca.Dispose();
        }
    }

    private static ResponseMessage Json(int status, string body) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") },
        BodyData = new BodyData { BodyAsString = body, DetectedBodyType = BodyType.String },
    };
}
