using ControlHome.Application.Security;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Security;

namespace ControlHome.Infrastructure.Tests.Safety;

public sealed class SafetyPolicyTests
{
    private static readonly CommandPrincipal _admin = CommandPrincipal.User(Guid.CreateVersion7(), "admin", [Roles.Admin]);
    private static readonly CommandPrincipal _member = CommandPrincipal.User(Guid.CreateVersion7(), "member", [Roles.Member]);
    private static readonly CommandPrincipal _viewer = CommandPrincipal.User(Guid.CreateVersion7(), "viewer", [Roles.Viewer]);

    public static TheoryData<string, string, string?, bool> Commands => new()
    {
        // capability, command, device class, gated (safety-critical and not fail-safe)
        { CapabilityIds.Switch, "turnOn", DeviceClasses.Light, false },
        { CapabilityIds.Lock, "unlock", DeviceClasses.Lock, true },
        { CapabilityIds.Lock, "lock", DeviceClasses.Lock, false },
        { CapabilityIds.DoorOpener, "open", null, true },
        { CapabilityIds.AlarmPanel, "disarm", null, true },
        { CapabilityIds.AlarmPanel, "arm", null, false },
        { CapabilityIds.Cover, "open", DeviceClasses.Gate, true },
        { CapabilityIds.Cover, "open", DeviceClasses.Garage, true },
        { CapabilityIds.Cover, "setPosition", DeviceClasses.Gate, true },
        { CapabilityIds.Cover, "open", "blind", false },
        { CapabilityIds.Cover, "close", DeviceClasses.Gate, false },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Members_and_admins_need_a_step_up_exactly_for_gated_commands(string capability, string command, string? deviceClass, bool gated)
    {
        var definition = CapabilityCatalog.Find(capability)!.FindCommand(command)!;
        var expected = gated ? SafetyDecision.StepUpRequired : SafetyDecision.Allowed;

        SafetyPolicy.IsGated(definition, deviceClass).ShouldBe(gated);
        SafetyPolicy.Evaluate(_member, definition, deviceClass).ShouldBe(expected);
        SafetyPolicy.Evaluate(_admin, definition, deviceClass).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Viewers_never_send_commands(string capability, string command, string? deviceClass, bool gated)
    {
        _ = gated;
        var definition = CapabilityCatalog.Find(capability)!.FindCommand(command)!;

        SafetyPolicy.Evaluate(_viewer, definition, deviceClass).ShouldBe(SafetyDecision.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void System_principals_need_allow_safety_critical_for_gated_commands(string capability, string command, string? deviceClass, bool gated)
    {
        var definition = CapabilityCatalog.Find(capability)!.FindCommand(command)!;

        SafetyPolicy.Evaluate(CommandPrincipal.System("automation:1"), definition, deviceClass)
            .ShouldBe(gated ? SafetyDecision.Forbidden : SafetyDecision.Allowed);
        SafetyPolicy.Evaluate(CommandPrincipal.System("automation:1", allowSafetyCritical: true), definition, deviceClass)
            .ShouldBe(SafetyDecision.Allowed);
    }
}
