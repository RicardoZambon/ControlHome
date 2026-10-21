using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Flow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>The setup / re-authorization flow against a fake SmartThings (token endpoint and locations).</summary>
public sealed class SetupFlowTests : IDisposable
{
    private const string Location1 = FakeSmartThings.LocationId;
    private const string Location2 = "00000000-0000-4000-8000-0000000010c1";

    private readonly FakeSmartThings _fake = new();
    private readonly FakeTimeProvider _time = new(StFixtures.Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task Setup_starts_with_the_choice_and_shows_the_data_driven_instructions()
    {
        await using var flow = Flow();

        var mode = (ChooseStep)await flow.StartAsync(Context(), Ct);
        mode.Choices.Select(c => c.Key).ShouldBe(["oauth", "pat"]);
        mode.Choices.ShouldAllBe(c => c.Label.PtBr.Length > 0 && c.Label.En != c.Label.PtBr);

        var instructions = (ExternalUrlStep)await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("oauth"), Ct);
        instructions.Url.Scheme.ShouldBe("https");
        instructions.Url.AbsoluteUri.ShouldEndWith("docs/connectors/smartthings.md#create-your-own-smartthings-app");
        instructions.PasteBack.ShouldBeNull();
        instructions.Description!.En.ShouldContain("npx @smartthings/cli apps:create");
        instructions.Description.En.ShouldContain("r:locations:* r:devices:* x:devices:*");
        instructions.Description.PtBr.ShouldContain("npx @smartthings/cli apps:create");
        instructions.Description.PtBr.ShouldContain("https://httpbin.org/get");

        var app = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.InstructionsStep, Empty(), Ct);
        app.Values!.Value.GetProperty("redirectUri").GetString().ShouldBe("https://httpbin.org/get");
        app.Values!.Value.GetProperty("endpoints").GetString().ShouldBe("documented");
        app.Fields.Single(f => f.Key == "clientSecret").Secret.ShouldBeTrue();
        app.Fields.Single(f => f.Key == "redirectUri").Help!.En.ShouldContain("httpbin.org receives the one-time sign-in code");
    }

    [Fact]
    public async Task The_oauth_flow_signs_in_exchanges_the_code_with_basic_auth_and_finishes_with_the_tokens_as_secrets()
    {
        TokenEndpoint();
        Locations(Location1);
        await using var flow = Flow();
        var authorize = await ToAuthorizeAsync(flow);

        authorize.Url.Host.ShouldBe("auth.example.test");
        var query = authorize.Url.Query;
        query.ShouldContain("client_id=" + FakeSmartThings.ClientId);
        query.ShouldContain("response_type=code");
        query.ShouldContain("redirect_uri=https%3A%2F%2Fhttpbin.org%2Fget");
        query.ShouldContain("state=" + flow.State);
        authorize.PasteBack!.Secret.ShouldBeTrue();

        var done = (DoneStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"https://httpbin.org/get?code=code-0001&state={flow.State}"), Ct);

        // The exchange: HTTP Basic client authentication, the code and the exact redirect URI.
        var exchange = _fake.Requests("/oauth/token", "POST").ShouldHaveSingleItem().RequestMessage!;
        exchange.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakeSmartThings.ClientId}:{FakeSmartThings.ClientSecret}")));
        exchange.Body!.ShouldContain("grant_type=authorization_code");
        exchange.Body!.ShouldContain("code=code-0001");
        exchange.Body!.ShouldContain("redirect_uri=https%3A%2F%2Fhttpbin.org%2Fget");
        _fake.Requests("/v1/locations").ShouldHaveSingleItem().RequestMessage!.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Bearer access-token-0001");

        done.Name.ShouldBe("SmartThings (Home)");
        done.Config.GetProperty("mode").GetString().ShouldBe("oauth");
        done.Config.GetProperty("clientId").GetString().ShouldBe(FakeSmartThings.ClientId);
        done.Config.GetProperty("locationId").GetString().ShouldBe(Location1);
        done.Config.GetProperty("endpoints").GetString().ShouldBe("documented");
        done.Secrets[SecretKeys.ClientSecret].ShouldBe(FakeSmartThings.ClientSecret);
        var tokens = TokenSet.Deserialize(done.Secrets[SecretKeys.Tokens]).ShouldNotBeNull();
        tokens.RefreshToken.ShouldBe("refresh-token-0001");
        tokens.ExpiresAtUtc.ShouldBe(StFixtures.Now.AddSeconds(86399));
        foreach (var secret in (string[])[FakeSmartThings.ClientSecret, "access-token-0001", "refresh-token-0001", "code-0001"])
        {
            done.Config.GetRawText().ShouldNotContain(secret);
            JsonSerializer.Serialize<FlowStep>(done).ShouldNotContain(secret);
        }

        // What the host does with the result: the config is valid for the connector.
        Should.NotThrow(() => Factory().CreateRuntime(new IntegrationInstance(Guid.NewGuid(), done.Name, done.Config, EmptySecretStore.Instance)));
    }

    [Fact]
    public async Task A_pasted_url_with_another_state_is_refused_without_calling_smartthings()
    {
        TokenEndpoint();
        Locations(Location1);
        await using var flow = Flow();
        await ToAuthorizeAsync(flow);
        var state = flow.State;

        var again = (ExternalUrlStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste("https://httpbin.org/get?code=stolen&state=attacker-state"), Ct);

        again.StepId.ShouldBe(SmartThingsSetupFlow.AuthorizeStep);
        again.Description!.En.ShouldContain("does not belong to this sign-in");
        again.Description.PtBr.ShouldContain("não pertence a este acesso");
        flow.State.ShouldBe(state);
        _fake.Requests("/oauth/token").ShouldBeEmpty();

        var noState = (ExternalUrlStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste("https://httpbin.org/get?code=stolen"), Ct);
        noState.StepId.ShouldBe(SmartThingsSetupFlow.AuthorizeStep);
        _fake.Requests("/oauth/token").ShouldBeEmpty();

        for (var i = 2; i < SmartThingsSetupFlow.MaxStateMismatches - 1; i++)
        {
            await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste("code=x&state=y"), Ct);
        }

        // The right URL still works until the limit …
        var done = await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"code=code-0001&state={state}"), Ct);
        done.ShouldBeOfType<DoneStep>();
    }

    [Fact]
    public async Task Too_many_foreign_pastes_abort_the_sign_in()
    {
        await using var flow = Flow();
        await ToAuthorizeAsync(flow);

        FlowStep step = null!;
        for (var i = 0; i < SmartThingsSetupFlow.MaxStateMismatches; i++)
        {
            step = await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste("code=x&state=y"), Ct);
        }

        step.ShouldBeOfType<AbortStep>().Reason.ShouldBe("state_mismatch");
        _fake.Requests("/oauth/token").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_httpbin_page_text_works_and_several_locations_are_a_choice()
    {
        TokenEndpoint();
        Locations(Location1, Location2);
        await using var flow = Flow();
        await ToAuthorizeAsync(flow);
        var page = new JsonObject
        {
            ["args"] = new JsonObject { ["code"] = "code-0001", ["state"] = flow.State },
            ["headers"] = new JsonObject { ["Host"] = "httpbin.org" },
            ["url"] = $"https://httpbin.org/get?code=code-0001&state={flow.State}",
        }.ToJsonString();

        var choose = (ChooseStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste(page), Ct);
        choose.Choices.Select(c => c.Key).ShouldBe([Location1, Location2]);
        choose.Choices.Select(c => c.Label.En).ShouldBe(["Home", "Beach house"]);

        var done = (DoneStep)await flow.ContinueAsync(SmartThingsSetupFlow.LocationStep, Choice(Location2), Ct);
        done.Config.GetProperty("locationId").GetString().ShouldBe(Location2);
        done.Name.ShouldBe("SmartThings (Beach house)");
    }

    [Fact]
    public async Task A_spent_code_signs_in_again_and_a_rejected_client_goes_back_to_the_app_form()
    {
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost()).InScenario("exchange").WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("""{"error":"invalid_grant"}"""));
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost()).InScenario("exchange").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("Content-Type", "application/json").WithBody("""{"error":"invalid_client"}"""));
        await using var flow = Flow();
        await ToAuthorizeAsync(flow);
        var firstState = flow.State;

        var retry = (ExternalUrlStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"code=old&state={firstState}"), Ct);
        retry.Description!.En.ShouldContain("can no longer be used");
        flow.State.ShouldNotBe(firstState); // a fresh sign-in, a fresh state

        var form = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"code=new&state={flow.State}"), Ct);
        form.StepId.ShouldBe(SmartThingsSetupFlow.AppStep);
        form.Errors.Keys.ShouldBe(["clientSecret"]);
        form.Values!.Value.TryGetProperty("clientSecret", out _).ShouldBeFalse();
        form.Values!.Value.GetProperty("clientId").GetString().ShouldBe(FakeSmartThings.ClientId);
    }

    [Fact]
    public async Task The_app_form_validates_the_redirect_and_custom_oauth_urls()
    {
        await using var flow = Flow();
        await flow.StartAsync(Context(), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("oauth"), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.InstructionsStep, Empty(), Ct);

        var form = (FormStep)await flow.ContinueAsync(
            SmartThingsSetupFlow.AppStep,
            Json(new JsonObject
            {
                ["clientId"] = FakeSmartThings.ClientId,
                ["clientSecret"] = FakeSmartThings.ClientSecret,
                ["redirectUri"] = "http://192.0.2.10/callback",
                ["endpoints"] = "custom",
                ["authorizeUrl"] = "https://evil.example.test/authorize",
                ["tokenUrl"] = "https://auth-global.api.smartthings.com/oauth/token",
            }),
            Ct);

        form.Errors.Keys.ShouldBe(["redirectUri", "authorizeUrl"], ignoreOrder: true);
        form.Values!.Value.TryGetProperty("clientSecret", out _).ShouldBeFalse();

        var missing = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.AppStep, Json(new JsonObject { ["redirectUri"] = "https://httpbin.org/get", ["endpoints"] = "documented" }), Ct);
        missing.Errors.Keys.ShouldContain("clientId");
    }

    [Fact]
    public async Task The_personal_access_token_mode_is_for_development_with_a_24_hour_warning()
    {
        Locations(Location1);
        await using var flow = Flow();
        await flow.StartAsync(Context(), Ct);

        var form = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("pat"), Ct);
        form.Description!.En.ShouldContain("expire 24 hours after they were created");
        form.Description.PtBr.ShouldContain("expiram 24 horas depois de criados");
        form.Fields.ShouldHaveSingleItem().Secret.ShouldBeTrue();

        var done = (DoneStep)await flow.ContinueAsync(SmartThingsSetupFlow.TokenStep, Json(new JsonObject { ["personalAccessToken"] = "pat-0001" }), Ct);

        _fake.Requests("/v1/locations").ShouldHaveSingleItem().RequestMessage!.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Bearer pat-0001");
        done.Config.GetProperty("mode").GetString().ShouldBe("pat");
        done.Config.GetProperty("patIssuedAtUtc").GetString().ShouldStartWith("2026-10-21T12:00:00");
        done.Secrets.ShouldBe(new Dictionary<string, string> { [SecretKeys.PersonalAccessToken] = "pat-0001" });
        done.Config.GetRawText().ShouldNotContain("pat-0001");
        Should.NotThrow(() => Factory().CreateRuntime(new IntegrationInstance(Guid.NewGuid(), done.Name, done.Config, EmptySecretStore.Instance)));
    }

    [Fact]
    public async Task A_rejected_token_or_a_paid_plan_is_explained()
    {
        _fake.Server.Given(Request.Create().WithPath("/v1/locations").UsingGet()).InScenario("plan").WillSetStateTo("paid")
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("""{"error":{"code":"Unauthorized"}}"""));
        _fake.Server.Given(Request.Create().WithPath("/v1/locations").UsingGet()).InScenario("plan").WhenStateIs("paid")
            .RespondWith(Response.Create().WithStatusCode(402).WithBody("""{"error":{"code":"PaymentRequired","message":"A plan is required"}}"""));
        await using var flow = Flow();
        await flow.StartAsync(Context(), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("pat"), Ct);

        var rejected = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.TokenStep, Json(new JsonObject { ["personalAccessToken"] = "pat-expired" }), Ct);
        rejected.Errors["personalAccessToken"].En.ShouldContain("rejected this token");

        var plan = (AbortStep)await flow.ContinueAsync(SmartThingsSetupFlow.TokenStep, Json(new JsonObject { ["personalAccessToken"] = "pat-0002" }), Ct);
        plan.Reason.ShouldBe("plan_required");
        plan.Message.PtBr.ShouldContain("Personal Plan");
    }

    [Fact]
    public async Task Reauthorizing_keeps_the_location_and_reuses_the_stored_secret_for_the_same_app()
    {
        TokenEndpoint();
        Locations(Location1, Location2);
        var existing = Existing(new MemorySecretStore(new Dictionary<string, string> { [SecretKeys.ClientSecret] = "stored-secret-0001" }));
        await using var flow = Flow();

        var mode = (ChooseStep)await flow.StartAsync(Context(existing), Ct);
        mode.Description!.En.ShouldContain("are kept");
        await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("oauth"), Ct);
        var app = (FormStep)await flow.ContinueAsync(SmartThingsSetupFlow.InstructionsStep, Empty(), Ct);
        app.Values!.Value.GetProperty("clientId").GetString().ShouldBe(FakeSmartThings.ClientId);
        app.Fields.Single(f => f.Key == "clientSecret").Required.ShouldBeFalse();

        // A blank secret keeps the stored one (same client id).
        await flow.ContinueAsync(SmartThingsSetupFlow.AppStep, Json(new JsonObject
        {
            ["clientId"] = FakeSmartThings.ClientId,
            ["redirectUri"] = "https://httpbin.org/get",
            ["endpoints"] = "documented",
        }), Ct);
        var done = (DoneStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"code=code-0001&state={flow.State}"), Ct);

        _fake.Requests("/oauth/token").ShouldHaveSingleItem().RequestMessage!.Headers!["Authorization"].ShouldHaveSingleItem()
            .ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakeSmartThings.ClientId}:stored-secret-0001")));
        done.Name.ShouldBe("My SmartThings");
        done.Config.GetProperty("locationId").GetString().ShouldBe(Location1); // no choice: its own location
        done.Config.GetProperty("pollIntervalSeconds").GetInt32().ShouldBe(45);
        done.Secrets[SecretKeys.ClientSecret].ShouldBe("stored-secret-0001");
    }

    [Fact]
    public async Task Reauthorizing_with_another_location_is_refused()
    {
        TokenEndpoint();
        Locations(Location2);
        var existing = Existing(new MemorySecretStore(new Dictionary<string, string> { [SecretKeys.ClientSecret] = "stored-secret-0001" }));
        await using var flow = Flow();
        await flow.StartAsync(Context(existing), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("oauth"), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.InstructionsStep, Empty(), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.AppStep, Json(new JsonObject
        {
            ["clientId"] = "another-client",
            ["clientSecret"] = "another-secret",
            ["redirectUri"] = "https://httpbin.org/get",
            ["endpoints"] = "homeAssistant",
        }), Ct);

        var abort = (AbortStep)await flow.ContinueAsync(SmartThingsSetupFlow.AuthorizeStep, Paste($"code=code-0001&state={flow.State}"), Ct);

        abort.Reason.ShouldBe("location_mismatch");
        _fake.Requests("/oauth/ha-token").Count.ShouldBe(1); // the Home Assistant preset's token URL
    }

    private SmartThingsConnectorFactory Factory() => new(FakeSmartThings.Http, null, _fake.Options());

    private SmartThingsSetupFlow Flow() => (SmartThingsSetupFlow)Factory().CreateSetupFlow()!;

    private FlowContext Context(IntegrationInstance? existing = null) => new(_time, NullLogger.Instance, existing);

    private static IntegrationInstance Existing(ISecretStore secrets) => new(
        Guid.NewGuid(),
        "My SmartThings",
        StFixtures.Parse(StHarness.OAuthConfig(pollSeconds: 45)),
        secrets);

    private async Task<ExternalUrlStep> ToAuthorizeAsync(SmartThingsSetupFlow flow)
    {
        await flow.StartAsync(Context(), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.ModeStep, Choice("oauth"), Ct);
        await flow.ContinueAsync(SmartThingsSetupFlow.InstructionsStep, Empty(), Ct);
        return (ExternalUrlStep)await flow.ContinueAsync(
            SmartThingsSetupFlow.AppStep,
            Json(new JsonObject
            {
                ["clientId"] = FakeSmartThings.ClientId,
                ["clientSecret"] = FakeSmartThings.ClientSecret,
                ["redirectUri"] = "https://httpbin.org/get",
                ["endpoints"] = "documented",
            }),
            Ct);
    }

    private void TokenEndpoint()
    {
        var body = new JsonObject
        {
            ["access_token"] = "access-token-0001",
            ["token_type"] = "bearer",
            ["refresh_token"] = "refresh-token-0001",
            ["expires_in"] = 86399,
            ["scope"] = "r:locations:* r:devices:* x:devices:*",
        }.ToJsonString();
        foreach (var path in (string[])["/oauth/token", "/oauth/ha-token"])
        {
            _fake.Server.Given(Request.Create().WithPath(path).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
        }
    }

    private void Locations(params string[] ids)
    {
        var names = new Dictionary<string, string> { [Location1] = "Home", [Location2] = "Beach house" };
        var body = new JsonObject
        {
            ["items"] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["locationId"] = id, ["name"] = names[id] })]),
            ["_links"] = null,
        }.ToJsonString();
        _fake.Server.Given(Request.Create().WithPath("/v1/locations").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
    }

    private static JsonElement Choice(string key) => Json(new JsonObject { ["choice"] = key });

    private static JsonElement Paste(string text) => Json(new JsonObject { [SmartThingsSetupFlow.RedirectedUrlField] = text });

    private static JsonElement Empty() => Json([]);

    private static JsonElement Json(JsonObject value) => JsonSerializer.SerializeToElement(value);
}
