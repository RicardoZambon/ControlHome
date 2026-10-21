using System.Globalization;
using System.Net;
using System.Text;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Flow;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Runtime;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>Energy reports, rate-limit headers, error classification, the authorization request and the per-device limiter.</summary>
public sealed class SmartThingsPrimitivesTests
{
    private static readonly DateTimeOffset _now = StFixtures.Now;

    // ---- powerConsumptionReport ---------------------------------------------------------------------------------------

    [Fact]
    public void A_power_consumption_report_is_one_window_in_wh()
    {
        var status = StFixtures.Status(StFixtures.Ac).Get("main", "powerConsumptionReport", "powerConsumption")!;

        var window = PowerConsumptionReport.Parse(status.Value, "main", "DA-AC-RAC-000001").ShouldNotBeNull();

        window.StartUtc.ShouldBe(DateTimeOffset.Parse("2025-02-09T15:45:29Z", CultureInfo.InvariantCulture));
        window.EndUtc.ShouldBe(DateTimeOffset.Parse("2025-02-09T16:15:33Z", CultureInfo.InvariantCulture));
        window.Duration.ShouldBe(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(4));
        window.DeltaWh.ShouldBe(400);
        window.EnergyWh.ShouldBe(2_247_300);
        window.PersistedEnergyWh.ShouldBe(2_247_300);
        window.PowerW.ShouldBe(0);
    }

    [Fact]
    public void A_fridge_report_carries_its_power_and_an_ehs_heat_pump_reports_kilowatts()
    {
        var fridge = StFixtures.Status(StFixtures.Fridge).Get("main", "powerConsumptionReport", "powerConsumption")!.Value;

        PowerConsumptionReport.Parse(fridge, "main", "DA-REF-NORMAL-000001")!.PowerW.ShouldBe(74);
        PowerConsumptionReport.Parse(fridge, "main", "DA-REF-NORMAL-000001")!.PowerEnergyWh!.Value.ShouldBe(20.57, 0.01);

        var ehs = StFixtures.Parse("""{"energy":10,"deltaEnergy":5,"power":1.2,"start":"2026-10-21T10:00:00Z","end":"2026-10-21T10:15:00Z"}""");
        PowerConsumptionReport.Parse(ehs, "main", "DA-SAC-EHS-000001-SUB")!.PowerW.ShouldBe(1200);
    }

    [Theory]
    [InlineData("""{"energy":10,"deltaEnergy":5}""")]
    [InlineData("""{"energy":10,"deltaEnergy":5,"start":"2026-10-21T10:15:00Z","end":"2026-10-21T10:00:00Z"}""")]
    [InlineData("""{"energy":10,"deltaEnergy":5,"start":"1970-01-01T00:00:00Z","end":"2026-10-21T10:00:00Z"}""")]
    [InlineData("""{"energy":10,"deltaEnergy":-5,"start":"2026-10-21T10:00:00Z","end":"2026-10-21T10:15:00Z"}""")]
    [InlineData("""[1,2]""")]
    public void Reports_without_a_usable_window_are_ignored(string json)
    {
        PowerConsumptionReport.Parse(StFixtures.Parse(json), "main", null).ShouldBeNull();
    }

    [Fact]
    public void Repeated_polls_of_the_same_window_are_not_new()
    {
        var window = PowerConsumptionReport.Parse(StFixtures.Parse("""{"deltaEnergy":5,"start":"2026-10-21T10:00:00Z","end":"2026-10-21T10:15:00Z"}"""), "main", null)!;

        PowerConsumptionReport.IsNew(window, null).ShouldBeTrue();
        PowerConsumptionReport.IsNew(window, window.EndUtc).ShouldBeFalse();
        PowerConsumptionReport.IsNew(window, window.EndUtc.AddMinutes(-15)).ShouldBeTrue();
    }

    // ---- rate-limit headers -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(60d, false, 60_000d)] // small values: seconds (the rate-limit page)
    [InlineData(45_000d, false, 45_000d)] // above 1000: milliseconds (the Swagger spec)
    [InlineData(500d, true, 500d)] // a bucket that has shown milliseconds keeps counting in them
    [InlineData(5_000_000d, false, 900_000d)] // clamped to 15 minutes
    [InlineData(0d, false, 0d)]
    public void The_reset_header_unit_is_inferred(double raw, bool milliseconds, double expectedMs)
    {
        RateLimitHeaders.ResetAfter(raw, _now, milliseconds).ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void Epoch_reset_values_are_absolute()
    {
        RateLimitHeaders.ResetAfter(_now.AddSeconds(30).ToUnixTimeSeconds(), _now, milliseconds: false).ShouldBe(TimeSpan.FromSeconds(30));
        RateLimitHeaders.ResetAfter(_now.AddSeconds(20).ToUnixTimeMilliseconds(), _now, milliseconds: false).ShouldBe(TimeSpan.FromSeconds(20));
        RateLimitHeaders.ResetAfter(_now.AddSeconds(-20).ToUnixTimeSeconds(), _now, milliseconds: false).ShouldBe(TimeSpan.Zero);
        RateLimitHeaders.ResetAfter(null, _now, milliseconds: false).ShouldBeNull();
    }

    [Fact]
    public void A_budget_waits_for_the_reset_when_the_window_is_used_up()
    {
        var budget = new RateBudget();
        budget.Observe(Headers(limit: 12, remaining: 1, reset: "40000"), _now);

        budget.UsesMilliseconds.ShouldBeTrue();
        budget.AvailableAt(_now, calls: 1, reserve: 0).ShouldBe(_now);
        budget.AvailableAt(_now, calls: 1, reserve: 1).ShouldBe(_now.AddSeconds(40));
        budget.Headroom().ShouldBe(1d / 12, 1e-9);

        // Later answers of the same bucket in "seconds-looking" values are still milliseconds.
        budget.Observe(Headers(limit: 12, remaining: 0, reset: "900"), _now);
        budget.AvailableAt(_now).ShouldBe(_now.AddMilliseconds(900));

        budget.Block(_now.AddMinutes(2));
        budget.AvailableAt(_now).ShouldBe(_now.AddMinutes(2));
    }

    [Fact]
    public void Missing_or_garbled_headers_change_nothing()
    {
        var headers = Headers(limit: null, remaining: null, reset: null);
        headers.IsEmpty.ShouldBeTrue();

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "lots");
        RateLimitHeaders.Parse(response.Headers).Remaining.ShouldBeNull();
    }

    // ---- error classification -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "", ConnectorErrorKind.PlanRequired, IntegrationStatus.PlanRequired)]
    [InlineData(HttpStatusCode.Forbidden, """{"requestId":"r","error":{"code":"PlanRequired","message":"A paid SmartThings API plan is required."}}""", ConnectorErrorKind.PlanRequired, IntegrationStatus.PlanRequired)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Subscription expired"}}""", ConnectorErrorKind.PlanRequired, IntegrationStatus.PlanRequired)]
    [InlineData(HttpStatusCode.Forbidden, """{"requestId":"r","error":{"code":"Forbidden","message":"Access is denied"}}""", ConnectorErrorKind.Permanent, IntegrationStatus.Error)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"Unauthorized"}}""", ConnectorErrorKind.Unauthorized, IntegrationStatus.NeedsReauth)]
    [InlineData(HttpStatusCode.TooManyRequests, "", ConnectorErrorKind.RateLimited, IntegrationStatus.Degraded)]
    [InlineData(HttpStatusCode.BadGateway, "", ConnectorErrorKind.Transient, IntegrationStatus.Degraded)]
    [InlineData((HttpStatusCode)422, """{"error":{"code":"GuardrailError"}}""", ConnectorErrorKind.Permanent, IntegrationStatus.Error)]
    public void Failures_are_classified_and_plan_errors_are_recognized_defensively(HttpStatusCode status, string body, ConnectorErrorKind kind, IntegrationStatus integrationStatus)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        var failure = SmartThingsErrors.From(response, body, new FakeTimeProvider(_now), "GET /devices");

        failure.Kind.ShouldBe(kind);
        failure.Error.Status.ShouldBe(integrationStatus);
        failure.Message.ShouldStartWith($"SmartThings answered {(int)status}");
    }

    [Fact]
    public void Vendor_codes_are_sanitized_before_they_reach_a_message()
    {
        SmartThingsErrors.Sanitize("invalid_grant<script>alert(1)</script>").ShouldBe("invalid_grantscriptalert1script");
        SmartThingsErrors.Sanitize(new string('a', 100)).Length.ShouldBe(64);
    }

    // ---- authorization request ----------------------------------------------------------------------------------------

    [Fact]
    public void The_authorize_url_carries_the_app_the_scopes_and_a_random_state()
    {
        var endpoints = new OAuthEndpoints(new Uri("https://api.smartthings.com/v1/oauth/authorize"), new Uri("https://api.smartthings.com/v1/oauth/token"));
        var state = AuthorizationRequest.NewState();

        var url = AuthorizationRequest.BuildUrl(endpoints, "client-1", "https://httpbin.org/get", SetupInstructions.Default.Scopes, state);

        state.Length.ShouldBe(43);
        AuthorizationRequest.NewState().ShouldNotBe(state);
        url.GetLeftPart(UriPartial.Path).ShouldBe("https://api.smartthings.com/v1/oauth/authorize");
        url.Query.ShouldContain("client_id=client-1");
        url.Query.ShouldContain("response_type=code");
        url.Query.ShouldContain("redirect_uri=https%3A%2F%2Fhttpbin.org%2Fget");
        url.Query.ShouldContain("scope=r%3Alocations%3A%2A%20r%3Adevices%3A%2A%20x%3Adevices%3A%2A");
        url.Query.ShouldContain("state=" + state);
    }

    [Theory]
    [InlineData("https://httpbin.org/get?code=abc123&state=S1", "abc123", "S1", null)]
    [InlineData("  code=abc123&state=S1  ", "abc123", "S1", null)]
    [InlineData("https://example.test/cb#state=S1&code=abc123", "abc123", "S1", null)]
    [InlineData("https://httpbin.org/get?error=access_denied&state=S1", null, "S1", "access_denied")]
    [InlineData("""{"args":{"code":"abc123","state":"S1"},"headers":{},"url":"https://httpbin.org/get?code=abc123&state=S1"}""", "abc123", "S1", null)]
    [InlineData("""{"args":{},"url":"https://httpbin.org/get?code=abc%2B123&state=S1"}""", "abc+123", "S1", null)]
    [InlineData("https://httpbin.org/get?code=abc+123&state=S1", "abc 123", "S1", null)]
    public void The_pasted_redirect_is_read_from_a_url_a_query_or_the_httpbin_page(string pasted, string? code, string? state, string? error)
    {
        AuthorizationRequest.Parse(pasted).ShouldBe(new PastedRedirect(code, state, error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://httpbin.org/get")]
    [InlineData("just some text")]
    public void A_paste_without_a_code_is_nothing(string pasted)
    {
        AuthorizationRequest.Parse(pasted).ShouldBeNull();
    }

    [Fact]
    public void Only_the_issued_state_matches()
    {
        var state = AuthorizationRequest.NewState();

        AuthorizationRequest.StateMatches(state, state).ShouldBeTrue();
        AuthorizationRequest.StateMatches(state, state[..^1] + "x").ShouldBeFalse();
        AuthorizationRequest.StateMatches(state, state + "x").ShouldBeFalse();
        AuthorizationRequest.StateMatches(state, null).ShouldBeFalse();
    }

    // ---- per-device limiter ------------------------------------------------------------------------------------------

    [Fact]
    public void The_per_device_limit_holds_within_every_minute_on_the_connector_clock()
    {
        var time = new FakeTimeProvider(_now);
        using var limiter = new ClockedSlidingWindow(12, TimeSpan.FromMinutes(1), time);

        Enumerable.Range(0, 12).Count(_ => limiter.TryAcquire()).ShouldBe(12);
        limiter.TryAcquire().ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(55));
        limiter.TryAcquire().ShouldBeFalse(); // all twelve are still within the last minute
        time.Advance(TimeSpan.FromSeconds(5));
        limiter.Available.ShouldBe(12);
        limiter.TryAcquire(reserve: 11).ShouldBeTrue();
        limiter.TryAcquire(reserve: 11).ShouldBeFalse(); // keeps the reserve
    }

    // ---- token bundle ------------------------------------------------------------------------------------------------

    [Fact]
    public void Tokens_round_trip_as_one_secret_and_never_print()
    {
        var tokens = new TokenSet("access-1", "refresh-1", _now.AddHours(24), _now, "r:devices:*", "app-1");

        TokenSet.Deserialize(tokens.Serialize()).ShouldBe(tokens);
        TokenSet.Deserialize("not json").ShouldBeNull();
        TokenSet.Deserialize("""{"accessToken":"a"}""").ShouldBeNull();
        tokens.ToString().ShouldNotContain("access-1");
        tokens.ToString().ShouldNotContain("refresh-1");
        tokens.ToSecrets().Keys.ShouldBe([SecretKeys.Tokens, SecretKeys.AccessToken, SecretKeys.RefreshToken], ignoreOrder: true);
    }

    private static RateLimitHeaders Headers(long? limit, long? remaining, string? reset)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (limit is { } l)
        {
            response.Headers.Add("X-RateLimit-Limit", l.ToString(CultureInfo.InvariantCulture));
        }

        if (remaining is { } r)
        {
            response.Headers.Add("X-RateLimit-Remaining", r.ToString(CultureInfo.InvariantCulture));
        }

        if (reset is not null)
        {
            response.Headers.Add("X-RateLimit-Reset", reset);
        }

        return RateLimitHeaders.Parse(response.Headers);
    }
}
