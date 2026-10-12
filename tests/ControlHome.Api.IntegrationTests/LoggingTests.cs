using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

public sealed class TestingEnvironmentLoggingTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task No_log_files_are_written_in_the_testing_environment()
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(factory.DataPath, "logs")).ShouldBeFalse();
    }
}

public sealed class ProductionLoggingTests(ProductionApiFactory factory) : IClassFixture<ProductionApiFactory>
{
    [Fact]
    public async Task Logs_roll_daily_under_the_data_path()
    {
        using var client = factory.CreateApiClient();
        using var response = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        var logs = Path.Combine(factory.DataPath, "logs");
        Directory.Exists(logs).ShouldBeTrue();
        Directory.GetFiles(logs, "controlhome-*.log").ShouldNotBeEmpty();
    }
}
