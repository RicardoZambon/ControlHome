using System.Text.Json;
using ControlHome.Application.Capabilities;
using ControlHome.Application.Common;
using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>Checks that need no database: the EF model against the migrations, and the capability DTO mappings.</summary>
public sealed class ModelTests
{
    [Fact]
    public async Task The_model_has_no_changes_missing_from_the_migrations()
    {
        // A connection string is configured but never opened: comparing the model with the snapshot is offline.
        await using var services = InfrastructureHost.Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlHome"] = "Server=127.0.0.1,1;Database=ControlHome_Test_model;User Id=x;Password=y;Encrypt=True",
        });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();

        db.Database.HasPendingModelChanges().ShouldBeFalse("run `dotnet ef migrations add` for the model changes");
        db.Database.GetMigrations().ShouldContain(m => m.EndsWith("_DevicesCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_tables_have_the_planned_names_and_unique_keys()
    {
        using var services = InfrastructureHost.Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlHome"] = "Server=127.0.0.1,1;Database=ControlHome_Test_model;User Id=x;Password=y;Encrypt=True",
        });
        using var scope = services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>().Model;

        string Table<T>() => model.FindEntityType(typeof(T))!.GetTableName()!;
        string[][] UniqueIndexes<T>() => model.FindEntityType(typeof(T))!.GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => i.Properties.Select(p => p.Name).ToArray())
            .ToArray();

        Table<Domain.Devices.Integration>().ShouldBe("Integrations");
        Table<Domain.Devices.AttributeState>().ShouldBe("AttributeState");
        UniqueIndexes<Domain.Devices.Device>().ShouldBe([["IntegrationId", "ExternalId"]]);
        UniqueIndexes<Domain.Devices.Endpoint>().ShouldBe([["DeviceId", "Key"]]);
        UniqueIndexes<Domain.Devices.EndpointCapability>().ShouldBe([["EndpointId", "CapabilityId"]]);
        UniqueIndexes<Domain.Devices.DeviceAttribute>().ShouldBe([["EndpointCapabilityId", "Key"]]);
    }

    [Fact]
    public void The_capability_catalogue_maps_to_dtos()
    {
        CapabilityMapping.Catalog.Select(c => c.Id).ShouldBe(CapabilityCatalog.All.Select(c => c.Id));

        var energy = CapabilityMapping.Catalog.Single(c => c.Id == "energy").Attributes.Single();
        energy.StateClass.ShouldBe("total_increasing");
        energy.Category.ShouldBe("primary");
        energy.Unit.ShouldBe("Wh");

        var powerOn = CapabilityMapping.Catalog.Single(c => c.Id == "mediaPlayer").Commands.Single(c => c.Name == "powerOn");
        powerOn.TimeoutMs.ShouldBe(90_000);
        powerOn.Confirmation.ShouldBe("awaitState");

        var open = CapabilityMapping.Catalog.Single(c => c.Id == "cover").Commands.Single(c => c.Name == "open");
        open.SafetyCritical.ShouldBeFalse();
        open.SafetyCriticalForDeviceClasses.ShouldBe(["gate", "garage"]);
        CapabilityMapping.Catalog.Single(c => c.Id == "lock").Commands.Single(c => c.Name == "unlock").SafetyCritical.ShouldBeTrue();

        var start = CapabilityMapping.Catalog.Single(c => c.Id == "applianceCycle").Commands.Single(c => c.Name == "start");
        start.GateAttribute.ShouldBe("remoteControlEnabled");
        CapabilityMapping.Catalog.Single(c => c.Id == "level").Commands.Single().Arguments.Select(a => (a.Key, a.Required))
            .ShouldBe([("level", true), ("transitionMs", false)]);
    }

    [Fact]
    public void Invalid_arguments_become_a_commands_invalid_arguments_problem_with_field_codes()
    {
        var setLevel = CapabilityCatalog.Level.FindCommand("setLevel")!;
        using var arguments = JsonDocument.Parse("""{"level":120,"speed":1}""");
        var result = CommandValidator.Validate(setLevel, arguments.RootElement, null);

        var error = CommandArgumentErrors.ToAppError(result);

        error.Code.ShouldBe(ErrorCodes.CommandsInvalidArguments);
        error.Kind.ShouldBe(AppErrorKind.Validation);
        var fields = error.Extensions![AppError.ErrorsExtension].ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>()!;
        fields["arguments.level"].ShouldBe([ErrorCodes.CommandsArgumentOutOfRange]);
        fields["arguments.speed"].ShouldBe([ErrorCodes.CommandsArgumentUnknown]);

        using var notAnObject = JsonDocument.Parse("[]");
        var whole = CommandArgumentErrors.ToAppError(CommandValidator.Validate(setLevel, notAnObject.RootElement, null));
        whole.Extensions![AppError.ErrorsExtension].ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>()!["arguments"]
            .ShouldBe([ErrorCodes.CommandsArgumentInvalidType]);

        Should.Throw<ArgumentException>(() => CommandArgumentErrors.ToAppError(ArgumentValidationResult.Valid));
    }

    [Fact]
    public void Every_value_error_kind_has_an_error_code()
    {
        var codes = Enum.GetValues<ValueErrorKind>().Select(CommandArgumentErrors.CodeFor).ToList();

        codes.ShouldBeUnique();
        codes.ShouldAllBe(c => c.StartsWith("commands.argument_", StringComparison.Ordinal));
    }
}
