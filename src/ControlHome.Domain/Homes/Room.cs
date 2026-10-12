using ControlHome.Domain.Geometry;

namespace ControlHome.Domain.Homes;

/// <summary>A room drawn on a <see cref="Floor"/>: a simple polygon in the floor's centimetre coordinates.</summary>
public sealed class Room
{
    public int Id { get; set; }

    public int FloorId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Icon key (lucide name such as <c>sofa</c>, or a prefixed key such as <c>mdi:sofa</c>).</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>One of <see cref="RoomColors.Tokens"/> (<c>room-1</c> … <c>room-8</c>), a design token, never a raw color.</summary>
    public string ColorToken { get; set; } = RoomColors.Tokens[0];

    /// <summary>The outline (stored as JSON): at least 3 points, simple, inside the floor.</summary>
    public List<PointCm> Polygon { get; set; } = [];

    /// <summary>Position in the floor's room list (0-based).</summary>
    public int SortOrder { get; set; }
}

/// <summary>The room color tokens (<c>--room-1</c> … <c>--room-8</c> in the web design tokens, light and dark).</summary>
public static class RoomColors
{
    public static IReadOnlyList<string> Tokens { get; } =
        ["room-1", "room-2", "room-3", "room-4", "room-5", "room-6", "room-7", "room-8"];
}
