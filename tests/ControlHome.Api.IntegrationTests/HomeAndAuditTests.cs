using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ControlHome.Application.Auditing;
using ControlHome.Application.Homes;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

[Collection(AuthCollection.Name)]
public sealed class HomeTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string Password = "a long enough password";

    [Fact]
    public async Task Any_role_can_read_the_home()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("viewer", Password, Roles.Viewer, Ct);
        using var viewer = await Factory.LoginAsync("viewer", Password, Ct);

        var home = (await viewer.GetFromJsonAsync<HomeDto>("/api/home", Ct))!;

        home.Id.ShouldBe(1);
        home.Name.ShouldBe("Casa");
        home.TimeZoneId.ShouldBe("America/Sao_Paulo");
        home.Latitude.ShouldBe(-23.55);
        home.Longitude.ShouldBe(-46.63);
        home.Locale.ShouldBe("pt-BR");
        home.Currency.ShouldBe("BRL");
        home.PricePerKwh.ShouldBe(0.95m);
        home.SetupCompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.GetAsync("/api/home", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_updates_the_home_and_the_changed_fields_are_audited()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var update = new UpdateHomeRequest("Casa da Praia", "America/Recife", -8.05, -34.9, "pt-BR", "BRL", 1.10m);

        using var response = await admin.PutAsJsonAsync("/api/home", update, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var home = (await admin.GetFromJsonAsync<HomeDto>("/api/home", Ct))!;
        home.Name.ShouldBe("Casa da Praia");
        home.TimeZoneId.ShouldBe("America/Recife");
        home.PricePerKwh.ShouldBe(1.10m);
        var audit = (await AuditEntriesAsync("home.updated")).ShouldHaveSingleItem();
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("fields").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            ["name", "timeZoneId", "latitude", "longitude", "pricePerKwh"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Member_cannot_update_the_home()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        using var member = await Factory.LoginAsync("member", Password, Ct);

        using var response = await member.PutAsJsonAsync("/api/home", new UpdateHomeRequest("Mine", "UTC", null, null, "en", "USD", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetFromJsonAsync<HomeDto>("/api/home", Ct))!.Name.ShouldBe("Casa");
    }

    [Fact]
    public async Task Invalid_update_is_400_validation_failed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PutAsJsonAsync("/api/home", new UpdateHomeRequest("Casa", "Nowhere/Land", 95, null, "en", "BRL", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync(Ct)).GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["timeZoneId", "latitude"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("""{"name":"Casa","timeZoneId":null,"locale":"en","currency":"BRL"}""")]
    [InlineData("""{"name":"Casa","locale":"en","currency":"BRL"}""")]
    [InlineData("""{"name":"Casa","timeZoneId":"","locale":"en","currency":"BRL"}""")]
    public async Task Missing_time_zone_is_400_validation_failed_not_a_server_error(string body)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PutAsync("/api/home", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["timeZoneId"]);
        problem.GetProperty("errors").GetProperty("timeZoneId").GetArrayLength().ShouldBe(1); // one message, not two
    }

    [Theory]
    [InlineData("E. South America Standard Time")] // a Windows id
    [InlineData("Pacific Standard Time")]
    [InlineData("right/America/Sao_Paulo")] // tzdata file names that are no IANA zone
    [InlineData("Factory")]
    [InlineData("america/sao_paulo")] // not the canonical spelling
    [InlineData("localtime")]
    public async Task Time_zone_must_be_a_canonical_iana_id(string timeZoneId)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PutAsJsonAsync("/api/home", new UpdateHomeRequest("Casa", timeZoneId, null, null, "en", "BRL", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync(Ct)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["timeZoneId"]);
        (await admin.GetFromJsonAsync<HomeDto>("/api/home", Ct))!.TimeZoneId.ShouldBe("America/Sao_Paulo");
    }

    [Theory]
    [InlineData("America/Sao_Paulo")]
    [InlineData("America/Recife")]
    [InlineData("Europe/Lisbon")]
    [InlineData("UTC")]
    public async Task Canonical_iana_time_zones_are_accepted(string timeZoneId)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PutAsJsonAsync("/api/home", new UpdateHomeRequest("Casa", timeZoneId, null, null, "en", "BRL", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<HomeDto>("/api/home", Ct))!.TimeZoneId.ShouldBe(timeZoneId);
    }
}

[Collection(AuthCollection.Name)]
public sealed class AuditTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string Password = "a long enough password";

    [Fact]
    public async Task Admin_pages_through_entries_newest_first()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct); // setup.completed
        for (var i = 0; i < 3; i++)
        {
            (await Factory.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, Ct)).Dispose(); // auth.login.succeeded
        }

        var first = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/audit?take=3", Ct))!;
        first.Items.Count.ShouldBe(3);
        first.Items.Select(e => e.Action).ShouldAllBe(a => a == "auth.login.succeeded");
        first.Items.Select(e => e.Id).ShouldBeInOrder(SortDirection.Descending);
        first.Items[0].ActorType.ShouldBe("User");
        first.Items[0].ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
        first.NextCursor.ShouldBe(first.Items[^1].Id);

        var second = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"/api/audit?take=3&cursor={first.NextCursor}", Ct))!;
        second.Items.ShouldHaveSingleItem().Action.ShouldBe("setup.completed");
        second.Items[0].Id.ShouldBeLessThan(first.Items[^1].Id);
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Default_page_has_everything_when_there_are_few_entries()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        var page = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/audit", Ct))!;

        page.Items.ShouldHaveSingleItem().Action.ShouldBe("setup.completed");
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Member_gets_403()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        using var member = await Factory.LoginAsync("member", Password, Ct);

        using var response = await member.GetAsync("/api/audit", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
