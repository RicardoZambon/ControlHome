using System.Net;
using ControlHome.Application.Common;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

public sealed class SetupStatusTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task Fresh_database_is_ready_and_setup_not_completed()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        status.GetProperty("setupCompleted").GetBoolean().ShouldBeFalse();
        status.GetProperty("databaseState").GetString().ShouldBe("Ready");
        status.GetProperty("databaseConfigured").GetBoolean().ShouldBeTrue();
        status.GetProperty("version").GetString().ShouldBe(AppVersion.Informational);
    }
}
