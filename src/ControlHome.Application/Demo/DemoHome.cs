using ControlHome.Domain.Geometry;

namespace ControlHome.Application.Demo;

/// <summary>A floor of the demo home.</summary>
/// <param name="Key">Referenced by <see cref="DemoPlacement.FloorKey"/>.</param>
public sealed record DemoFloor(string Key, string NameEn, string NamePt, int Level, int WidthCm, int HeightCm, IReadOnlyList<DemoRoom> Rooms);

/// <summary>A room of the demo home (polygon in the floor's centimetres).</summary>
public sealed record DemoRoom(string NameEn, string NamePt, string Icon, string ColorToken, IReadOnlyList<PointCm> Polygon);

/// <summary>Where a Simulated demo device (or one endpoint of it) sits; <paramref name="EndpointKey"/> <c>null</c> places the whole device.</summary>
public sealed record DemoPlacement(string ExternalId, string? EndpointKey, string FloorKey, double XCm, double YCm);

/// <summary>
/// The demo home: two floors (Ground floor/Térreo, Upper floor/1º andar) with eight rooms, the Simulated connector's
/// <c>demo</c> preset (20 devices) placed on them — two cameras and the porch light outside every room — six favorites,
/// everything named in the home's language (the connector names its devices in it). Used by <c>--seed-demo</c>, the
/// setup's "Load demo home" and screenshots (which only ever show it).
/// </summary>
public static class DemoHome
{
    public const string ConnectorKey = "simulated";
    public const string IntegrationNameEn = "Demo home";
    public const string IntegrationNamePt = "Casa de demonstração";
    public const string Ground = "ground";
    public const string Upper = "upper";

    public static IReadOnlyList<DemoFloor> Floors { get; } =
    [
        new DemoFloor(
            Ground,
            "Ground floor",
            "Térreo",
            0,
            1600,
            1200,
            [
                new DemoRoom("Garage", "Garagem", "car", "room-5", Rect(100, 100, 600, 600)),
                new DemoRoom(
                    "Living room",
                    "Sala",
                    "sofa",
                    "room-1",
                    [P(600, 100), P(1200, 100), P(1200, 1000), P(900, 1000), P(900, 700), P(600, 700)]),
                new DemoRoom("Kitchen", "Cozinha", "cooking-pot", "room-2", Rect(1200, 100, 1500, 700)),
                new DemoRoom("Laundry", "Lavanderia", "washing-machine", "room-3", Rect(1200, 700, 1500, 1000)),
            ]),
        new DemoFloor(
            Upper,
            "Upper floor",
            "1º andar",
            1,
            1600,
            1200,
            [
                new DemoRoom("Bedroom", "Quarto", "bed-single", "room-4", Rect(600, 100, 1000, 550)),
                new DemoRoom("Suite", "Suíte", "bed-double", "room-6", Rect(1000, 100, 1500, 550)),
                new DemoRoom("Office", "Escritório", "laptop", "room-7", Rect(600, 550, 1000, 1000)),
                new DemoRoom("Bathroom", "Banheiro", "bath", "room-8", Rect(1000, 550, 1300, 850)),
            ]),
    ];

    // Markers keep clear of the room labels (drawn at each room's centroid), of each other, and of the plan's bottom-left
    // corner (the scale bar on phones).
    public static IReadOnlyList<DemoPlacement> Placements { get; } =
    [
        // Ground floor: living room.
        new("light-1", "main", Ground, 750, 250),
        new("tv-1", "main", Ground, 1100, 400),
        new("contact-1", "main", Ground, 650, 650),
        new("lock-1", "main", Ground, 720, 650),
        new("blinds-1", "main", Ground, 1150, 900),
        new("switch3-1", "l1", Ground, 740, 450),
        new("switch3-1", "l2", Ground, 1050, 650),
        new("switch3-1", "l3", Ground, 660, 770), // porch: outside every room

        // Kitchen and laundry.
        new("plug-1", "main", Ground, 1300, 200),
        new("plug-2", "main", Ground, 1440, 320),
        new("washer-1", "main", Ground, 1250, 950),
        new("tank-1", "main", Ground, 1450, 950),

        // Garage, gate and cameras (two of them outside).
        new("gate-1", "main", Ground, 150, 350),
        new("nvr-1", null, Ground, 550, 150),
        new("cam-1", "main", Ground, 130, 130),
        new("cam-2", "main", Ground, 570, 570),
        new("cam-3", "main", Ground, 300, 950),
        new("cam-4", "main", Ground, 540, 760),

        // Upper floor.
        new("ac-1", "main", Upper, 800, 150),
        new("light-2", "main", Upper, 900, 450),
        new("motion-1", "main", Upper, 650, 600),
        new("climate-1", "main", Upper, 1060, 800),
    ];

    /// <summary>Endpoints on the dashboard's favorites grid: (external device id, endpoint key).</summary>
    public static IReadOnlyList<(string ExternalId, string EndpointKey)> Favorites { get; } =
    [
        ("light-1", "main"), ("ac-1", "main"), ("lock-1", "main"), ("gate-1", "main"), ("tv-1", "main"), ("plug-1", "main"),
    ];

    /// <summary>
    /// The Simulated integration's config: the <c>demo</c> preset with its devices (and the rooms they suggest) named in
    /// <paramref name="language"/> (<c>pt-BR</c> or <c>en</c>).
    /// </summary>
    public static string IntegrationConfigJson(string language) =>
        $$"""{"preset":"demo","language":"{{(language == "pt-BR" ? "pt-BR" : "en")}}","seed":42,"simulateReauth":false}""";

    private static PointCm P(double x, double y) => new(x, y);

    private static List<PointCm> Rect(double left, double top, double right, double bottom) =>
        [P(left, top), P(right, top), P(right, bottom), P(left, bottom)];
}
