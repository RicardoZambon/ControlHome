using ControlHome.Application.Placements;

namespace ControlHome.Application.Floors;

/// <summary>A point on a floor plan in centimetres (origin: the floor's top-left corner; y grows downwards).</summary>
public sealed record PointCmDto(double X, double Y);

/// <summary>
/// The image under a floor plan: its top-left corner sits at (<paramref name="OffsetXCm"/>, <paramref name="OffsetYCm"/>)
/// and one image pixel measures <paramref name="CmPerPx"/> centimetres.
/// </summary>
/// <param name="FileId">A file uploaded with purpose <c>floor-background</c> (<c>POST /api/files</c>).</param>
/// <param name="Opacity">0 (invisible) to 1 (opaque).</param>
public sealed record FloorBackgroundDto(Guid FileId, double Opacity, double OffsetXCm, double OffsetYCm, double CmPerPx);

/// <summary>A floor in the floor list.</summary>
public sealed record FloorSummaryDto(int Id, string Name, int Level, int RoomCount);

/// <summary>A floor with its rooms and the devices placed on it. Send <paramref name="RowVersion"/> back with every change to it.</summary>
/// <param name="Level">Storey: 0 ground, negative below ground.</param>
/// <param name="WidthCm">Plan width (the SVG viewBox is <c>0 0 WidthCm HeightCm</c>).</param>
/// <param name="GridCm">Editor grid spacing.</param>
/// <param name="Rooms">In display order.</param>
/// <param name="RowVersion">Opaque concurrency token; a change sent with an older one gets 409 <c>floors.concurrency</c>.</param>
/// <param name="Placements">
/// The endpoints and devices on this floor (<c>PUT /api/placements</c>; not part of the layout's concurrency token).
/// </param>
public sealed record FloorDto(
    int Id,
    string Name,
    int Level,
    int WidthCm,
    int HeightCm,
    int GridCm,
    FloorBackgroundDto? Background,
    IReadOnlyList<RoomDto> Rooms,
    string RowVersion,
    IReadOnlyList<PlacementDto> Placements);

/// <summary>A room of a floor.</summary>
/// <param name="Icon">Icon key (e.g. <c>sofa</c>).</param>
/// <param name="ColorToken"><c>room-1</c> … <c>room-8</c>.</param>
/// <param name="Polygon">The outline (implicitly closed), in the floor's centimetre coordinates.</param>
/// <param name="AreaM2">Enclosed area in m², rounded to two decimals.</param>
public sealed record RoomDto(
    int Id,
    string Name,
    string Icon,
    string ColorToken,
    IReadOnlyList<PointCmDto> Polygon,
    double AreaM2,
    int SortOrder);

/// <summary>Creates an empty floor (Admin). Size defaults to 2000 × 1500 cm, grid 50 cm.</summary>
public sealed record CreateFloorRequest(string Name, int Level, int? WidthCm, int? HeightCm);

/// <summary>
/// Changes a floor's properties (Admin); rooms are kept. The size cannot shrink below a room (400 with
/// <c>errors.widthCm</c>/<c>errors.heightCm</c> = <c>geometry.out_of_bounds</c>).
/// </summary>
/// <param name="Background">The background image, or <c>null</c> to remove it.</param>
/// <param name="RowVersion">From the <see cref="FloorDto"/> being edited.</param>
public sealed record UpdateFloorRequest(
    string Name,
    int Level,
    int WidthCm,
    int HeightCm,
    int GridCm,
    FloorBackgroundDto? Background,
    string RowVersion);

/// <summary>
/// Replaces the floor's rooms atomically (Admin): listed rooms with an <c>id</c> are updated, rooms without one are
/// created, rooms not listed are deleted; the list order becomes the display order.
/// </summary>
/// <param name="RowVersion">From the <see cref="FloorDto"/> being edited.</param>
public sealed record SaveFloorLayoutRequest(IReadOnlyList<RoomInput> Rooms, string RowVersion);

/// <summary>A room in a layout save.</summary>
/// <param name="Id">An existing room of this floor, or <c>null</c> for a new room.</param>
/// <param name="Polygon">
/// At least 3 points, simple (no self-intersection), inside the floor and enclosing an area; violations are field errors
/// under <c>rooms[i].polygon</c> with codes <c>geometry.*</c>.
/// </param>
public sealed record RoomInput(int? Id, string Name, string Icon, string ColorToken, IReadOnlyList<PointCmDto> Polygon);

/// <summary>A room in the list of all rooms (for pickers).</summary>
public sealed record RoomListItemDto(int Id, string Name, int FloorId, string FloorName);
