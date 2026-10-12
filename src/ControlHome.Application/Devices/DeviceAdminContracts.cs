using ControlHome.Domain.Devices;
using FluentValidation;

namespace ControlHome.Application.Devices;

/// <summary>Renames a device, sets its icon and room (<c>PUT /api/devices/{id}</c>, Admin).</summary>
/// <param name="Name">1–128 characters.</param>
/// <param name="Icon">An icon key (lowercase letters, digits, <c>-</c> and <c>:</c>), or <c>null</c> for the class's default.</param>
/// <param name="RoomId">A room, or <c>null</c> for none.</param>
public sealed record UpdateDeviceRequest(string Name, string? Icon, int? RoomId);

/// <summary>Changes an endpoint (<c>PUT /api/endpoints/{id}</c>, Admin).</summary>
/// <param name="Name">The endpoint's own name, or <c>null</c> to show the device's.</param>
/// <param name="RoomId">The endpoint's own room, or <c>null</c> to inherit the device's.</param>
/// <param name="Hidden">Leave it out of dashboards and pickers.</param>
public sealed record UpdateEndpointRequest(string? Name, string? Icon, int? RoomId, bool Hidden);

/// <summary>Adds an endpoint to (or removes it from) the dashboard's favorites (<c>PUT /api/endpoints/{id}/favorite</c>, Member).</summary>
public sealed record SetFavoriteRequest(bool Favorite);

internal static class DeviceFieldRules
{
    public const string IconPattern = "^[a-z0-9:-]+$";
}

public sealed class UpdateDeviceRequestValidator : AbstractValidator<UpdateDeviceRequest>
{
    public UpdateDeviceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceLimits.NameMaxLength).Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("'{PropertyName}' must not be blank.");
        RuleFor(x => x.Icon).MaximumLength(DeviceLimits.IconMaxLength).Matches(DeviceFieldRules.IconPattern).When(x => x.Icon is not null);
        RuleFor(x => x.RoomId).GreaterThan(0).When(x => x.RoomId is not null);
    }
}

public sealed class UpdateEndpointRequestValidator : AbstractValidator<UpdateEndpointRequest>
{
    public UpdateEndpointRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(DeviceLimits.NameMaxLength).Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("'{PropertyName}' must not be blank.").When(x => x.Name is not null);
        RuleFor(x => x.Icon).MaximumLength(DeviceLimits.IconMaxLength).Matches(DeviceFieldRules.IconPattern).When(x => x.Icon is not null);
        RuleFor(x => x.RoomId).GreaterThan(0).When(x => x.RoomId is not null);
    }
}

public sealed class SetFavoriteRequestValidator : AbstractValidator<SetFavoriteRequest>
{
}
