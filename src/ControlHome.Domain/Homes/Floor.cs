namespace ControlHome.Domain.Homes;

/// <summary>
/// One storey of the home: a plan of <see cref="WidthCm"/> × <see cref="HeightCm"/> centimetres (origin top-left) holding
/// <see cref="Rooms"/>, optionally drawn over a background image. <see cref="RowVersion"/> changes on every save, so two
/// admins editing the same floor cannot overwrite each other.
/// </summary>
public sealed class Floor
{
    public const int DefaultWidthCm = 2000;
    public const int DefaultHeightCm = 1500;
    public const int DefaultGridCm = 50;

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Storey number: 0 ground, negative below ground. Floors are listed by level.</summary>
    public int Level { get; set; }

    public int WidthCm { get; set; } = DefaultWidthCm;

    public int HeightCm { get; set; } = DefaultHeightCm;

    /// <summary>Editor grid spacing (snapping).</summary>
    public int GridCm { get; set; } = DefaultGridCm;

    /// <summary>The traced plan image, if any (stored as JSON).</summary>
    public FloorBackground? Background { get; set; }

    /// <summary>SQL Server <c>rowversion</c>: the optimistic concurrency token of the floor and its layout.</summary>
    public byte[] RowVersion { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<Room> Rooms { get; set; } = [];
}

/// <summary>
/// A background image placed under the plan: the image's top-left corner sits at (<see cref="OffsetXCm"/>,
/// <see cref="OffsetYCm"/>) and one image pixel measures <see cref="CmPerPx"/> centimetres (from calibration).
/// </summary>
public sealed class FloorBackground
{
    /// <summary>The <see cref="Files.StoredFile"/> holding the image (purpose <c>floor-background</c>).</summary>
    public Guid FileId { get; set; }

    /// <summary>0 (invisible) to 1 (opaque).</summary>
    public double Opacity { get; set; } = 1;

    public double OffsetXCm { get; set; }

    public double OffsetYCm { get; set; }

    public double CmPerPx { get; set; } = 1;
}
