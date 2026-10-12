using ControlHome.Application.Common;
using ControlHome.Domain.Geometry;
using ControlHome.Domain.Homes;
using FluentValidation;

namespace ControlHome.Application.Floors;

/// <summary>Limits of floors and rooms (shared by the validators, the service and the EF configuration).</summary>
public static class FloorRules
{
    public const int NameMaxLength = 80;
    public const int MinLevel = -10;
    public const int MaxLevel = 100;
    public const int MinSizeCm = 100;
    public const int MaxSizeCm = 100_000;
    public const int MinGridCm = 1;
    public const int MaxGridCm = 1_000;
    public const int MaxRooms = 200;
    public const int MaxPolygonPoints = 500;
    public const int IconMaxLength = 64;
    public const int ColorTokenMaxLength = 16;

    /// <summary>Rooms smaller than this (cm²) count as having no area.</summary>
    public const double MinRoomAreaCm2 = 1;

    /// <summary>How far a background image may be moved from the origin, either way.</summary>
    public const double MaxBackgroundOffsetCm = 1_000_000;
    public const double MinCmPerPx = 0.001;
    public const double MaxCmPerPx = 1_000;

    /// <summary>Lower-case kebab segments, optionally prefixed (<c>sofa</c>, <c>bed-double</c>, <c>mdi:sofa-outline</c>).</summary>
    public const string IconPattern = "^[a-z0-9]+(?:[-:][a-z0-9]+)*$";

    /// <summary>SQL Server <c>rowversion</c> values are 8 bytes; the API exchanges them as base64.</summary>
    private const int RowVersionLength = 8;

    public static string FormatRowVersion(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static bool TryParseRowVersion(string? value, out byte[] rowVersion)
    {
        rowVersion = [];
        var buffer = new byte[RowVersionLength];
        if (string.IsNullOrEmpty(value) || !Convert.TryFromBase64String(value, buffer, out var written) || written != RowVersionLength)
        {
            return false;
        }

        rowVersion = buffer;
        return true;
    }
}

/// <summary>The geometry rules of a room outline on a floor of a given size.</summary>
public static class RoomPolygonRules
{
    /// <summary>
    /// The first rule <paramref name="polygon"/> breaks as an error code, or <c>null</c> when it is valid: fewer than 3
    /// distinct points (<c>geometry.too_few_points</c>), a point outside <c>0..widthCm × 0..heightCm</c>
    /// (<c>geometry.out_of_bounds</c>), all points on one line or less than 1 cm² enclosed (<c>geometry.zero_area</c>),
    /// or edges that cross or touch (<c>geometry.self_intersecting</c>).
    /// </summary>
    public static string? Validate(IReadOnlyList<PointCm> polygon, int widthCm, int heightCm)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        if (polygon.Count > FloorRules.MaxPolygonPoints)
        {
            return ErrorCodes.GeometryTooManyPoints;
        }

        if (polygon.Distinct().Count() < 3)
        {
            return ErrorCodes.GeometryTooFewPoints;
        }

        if (!polygon.All(p => IsWithin(p, widthCm, heightCm)))
        {
            return ErrorCodes.GeometryOutOfBounds;
        }

        var area = PolygonMath.AreaCm2(polygon);
        if (!PolygonMath.IsSimple(polygon))
        {
            // Points on one line enclose nothing (and their edges overlap): report that, not a crossing.
            return area < FloorRules.MinRoomAreaCm2 && AllCollinear(polygon)
                ? ErrorCodes.GeometryZeroArea
                : ErrorCodes.GeometrySelfIntersecting;
        }

        return area < FloorRules.MinRoomAreaCm2 ? ErrorCodes.GeometryZeroArea : null;
    }

    /// <summary>Whether the point lies on the floor (edges included). NaN and infinities never do.</summary>
    public static bool IsWithin(PointCm point, int widthCm, int heightCm)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.X >= 0 && point.X <= widthCm && point.Y >= 0 && point.Y <= heightCm;
    }

    /// <summary>Whether every point lies (within a micrometre) on the line through the first two distinct points.</summary>
    private static bool AllCollinear(IReadOnlyList<PointCm> polygon)
    {
        const double ToleranceCm = 1e-4;
        var distinct = polygon.Distinct().ToArray();
        var (first, second) = (distinct[0], distinct[1]);
        var (dx, dy) = (second.X - first.X, second.Y - first.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return distinct.All(p => Math.Abs((dx * (p.Y - first.Y)) - (dy * (p.X - first.X))) / length <= ToleranceCm);
    }
}

public sealed class CreateFloorRequestValidator : AbstractValidator<CreateFloorRequest>
{
    public CreateFloorRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(FloorRules.NameMaxLength);
        RuleFor(x => x.Level).InclusiveBetween(FloorRules.MinLevel, FloorRules.MaxLevel);
        RuleFor(x => x.WidthCm).InclusiveBetween(FloorRules.MinSizeCm, FloorRules.MaxSizeCm);
        RuleFor(x => x.HeightCm).InclusiveBetween(FloorRules.MinSizeCm, FloorRules.MaxSizeCm);
    }
}

public sealed class UpdateFloorRequestValidator : AbstractValidator<UpdateFloorRequest>
{
    public UpdateFloorRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(FloorRules.NameMaxLength);
        RuleFor(x => x.Level).InclusiveBetween(FloorRules.MinLevel, FloorRules.MaxLevel);
        RuleFor(x => x.WidthCm).InclusiveBetween(FloorRules.MinSizeCm, FloorRules.MaxSizeCm);
        RuleFor(x => x.HeightCm).InclusiveBetween(FloorRules.MinSizeCm, FloorRules.MaxSizeCm);
        RuleFor(x => x.GridCm).InclusiveBetween(FloorRules.MinGridCm, FloorRules.MaxGridCm);
        RuleFor(x => x.Background!).SetValidator(new FloorBackgroundDtoValidator()).When(x => x.Background is not null);
        RuleFor(x => x.RowVersion).RowVersion();
    }
}

public sealed class FloorBackgroundDtoValidator : AbstractValidator<FloorBackgroundDto>
{
    public FloorBackgroundDtoValidator()
    {
        RuleFor(x => x.FileId).NotEmpty();
        RuleFor(x => x.Opacity).InclusiveBetween(0, 1);
        RuleFor(x => x.OffsetXCm).InclusiveBetween(-FloorRules.MaxBackgroundOffsetCm, FloorRules.MaxBackgroundOffsetCm);
        RuleFor(x => x.OffsetYCm).InclusiveBetween(-FloorRules.MaxBackgroundOffsetCm, FloorRules.MaxBackgroundOffsetCm);
        RuleFor(x => x.CmPerPx).InclusiveBetween(FloorRules.MinCmPerPx, FloorRules.MaxCmPerPx);
    }
}

/// <summary>
/// The shape of a layout save. The geometry itself (which needs the floor's size) is checked by
/// <see cref="FloorService.SaveLayoutAsync"/> with <see cref="RoomPolygonRules"/>.
/// </summary>
public sealed class SaveFloorLayoutRequestValidator : AbstractValidator<SaveFloorLayoutRequest>
{
    public SaveFloorLayoutRequestValidator()
    {
        RuleFor(x => x.Rooms).NotNull();
        When(x => x.Rooms is not null, () =>
        {
            RuleFor(x => x.Rooms)
                .Must(rooms => rooms.Count <= FloorRules.MaxRooms)
                .WithMessage($"A floor can have at most {FloorRules.MaxRooms} rooms.")
                .Must(rooms => rooms.Where(r => r?.Id is not null).GroupBy(r => r.Id).All(g => g.Count() == 1))
                .WithMessage("A room may be listed only once.");
            RuleForEach(x => x.Rooms).NotNull().SetValidator(new RoomInputValidator());
        });
        RuleFor(x => x.RowVersion).RowVersion();
    }
}

public sealed class RoomInputValidator : AbstractValidator<RoomInput>
{
    public RoomInputValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).When(x => x.Id is not null);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(FloorRules.NameMaxLength);
        RuleFor(x => x.Icon)
            .NotEmpty()
            .MaximumLength(FloorRules.IconMaxLength)
            .Matches(FloorRules.IconPattern)
            .WithMessage("'{PropertyName}' must be an icon key such as 'sofa' or 'mdi:sofa-outline'.");
        RuleFor(x => x.ColorToken)
            .NotEmpty()
            .Must(token => RoomColors.Tokens.Contains(token, StringComparer.Ordinal))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", RoomColors.Tokens)}.");
        RuleFor(x => x.Polygon).NotNull();
        RuleFor(x => x.Polygon)
            .Must(polygon => polygon.Count <= FloorRules.MaxPolygonPoints)
            .WithMessage(ErrorCodes.GeometryTooManyPoints)
            .When(x => x.Polygon is not null);
        RuleForEach(x => x.Polygon).NotNull();
    }
}

internal static class FloorValidationExtensions
{
    public static IRuleBuilderOptions<T, string> RowVersion<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(value => FloorRules.TryParseRowVersion(value, out _))
            .WithMessage("'{PropertyName}' must be the rowVersion of the floor being edited.");
}
