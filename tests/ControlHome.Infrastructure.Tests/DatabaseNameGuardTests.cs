using ControlHome.Infrastructure.Persistence;

namespace ControlHome.Infrastructure.Tests;

public sealed class DatabaseNameGuardTests
{
    [Theory]
    [InlineData("ControlHome_Dev")]
    [InlineData("ControlHome_Dev_topic1")]
    [InlineData("ControlHome_Test_ab12")]
    [InlineData("ControlHome_Test_0a1b2c3d4e5f")]
    [InlineData("ControlHome_Compose")]
    public void Allows_dev_test_and_compose_names(string name)
    {
        DatabaseNameGuard.IsAllowed(name).ShouldBeTrue();
        Should.NotThrow(() => DatabaseNameGuard.EnsureAllowed(name));
    }

    [Theory]
    [InlineData("GeekVault")]
    [InlineData("ControlHome_Dev;DROP")]
    [InlineData("ControlHome_Dev;DROP DATABASE GeekVault")]
    [InlineData("master")]
    [InlineData("controlhome_dev")]
    [InlineData("CONTROLHOME_DEV")]
    [InlineData("ControlHome_Prod")]
    [InlineData("ControlHome_Test_")]
    [InlineData("ControlHome_Test_a-b")]
    [InlineData("ControlHome_Dev\n")]
    [InlineData(" ControlHome_Dev")]
    [InlineData("ControlHome")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_everything_else(string? name)
    {
        DatabaseNameGuard.IsAllowed(name).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => DatabaseNameGuard.EnsureAllowed(name));
    }

    [Fact]
    public void Production_name_only_on_the_apps_own_create_path()
    {
        DatabaseNameGuard.IsAllowed("ControlHome", allowProduction: true).ShouldBeTrue();
        DatabaseNameGuard.IsAllowed("controlhome", allowProduction: true).ShouldBeFalse();
        DatabaseNameGuard.IsAllowed("GeekVault", allowProduction: true).ShouldBeFalse();
        Should.NotThrow(() => DatabaseNameGuard.EnsureAllowed("ControlHome", allowProduction: true));
    }
}
