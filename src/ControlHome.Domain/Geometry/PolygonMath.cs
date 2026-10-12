namespace ControlHome.Domain.Geometry;

/// <summary>
/// Plane geometry for floor plans. A polygon is a list of vertices in centimetres, implicitly closed (the last vertex
/// connects back to the first; do not repeat the first point) and in either winding order. The web editor mirrors these
/// rules in <c>geometry.ts</c> with the same tests.
/// </summary>
/// <remarks>
/// "Collinear" and "on a segment" use a small relative tolerance, so floating-point noise in coordinates that came from
/// the editor (e.g. a vertex snapped onto a diagonal wall) does not flip a result.
/// </remarks>
public static class PolygonMath
{
    /// <summary>Relative tolerance: sin(angle) below this counts as collinear.</summary>
    private const double RelativeEpsilon = 1e-9;

    /// <summary>The enclosed area in cm² (absolute value of the shoelace formula); 0 for fewer than 3 points.</summary>
    public static double AreaCm2(IReadOnlyList<PointCm> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        return polygon.Count < 3 ? 0 : Math.Abs(SignedDoubleArea(polygon)) / 2;
    }

    /// <summary>
    /// True when the polygon is simple: at least 3 distinct points, no repeated consecutive vertex, and no two edges
    /// touch except consecutive edges at their shared vertex (so no crossing, no pinch, no edge folding back over the
    /// previous one, and not all points on one line).
    /// </summary>
    public static bool IsSimple(IReadOnlyList<PointCm> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var count = polygon.Count;
        if (polygon.Distinct().Count() < 3)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (polygon[i] == polygon[(i + 1) % count])
            {
                return false;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var a1 = polygon[i];
            var a2 = polygon[(i + 1) % count];
            for (var j = i + 1; j < count; j++)
            {
                var b1 = polygon[j];
                var b2 = polygon[(j + 1) % count];
                var intersects = j == i + 1
                    ? FoldsBack(a1, a2, b2) // edges a1→a2 and a2→b2 share a2
                    : i == 0 && j == count - 1
                        ? FoldsBack(b1, a1, a2) // edges b1→a1 and a1→a2 share a1
                        : SegmentsIntersect(a1, a2, b1, b2);
                if (intersects)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="point"/> is inside the polygon or on its boundary (ray casting, with an explicit
    /// boundary test first). Always false for fewer than 3 points.
    /// </summary>
    public static bool Contains(IReadOnlyList<PointCm> polygon, PointCm point)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(point);
        var count = polygon.Count;
        if (count < 3)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (IsOnSegment(polygon[i], polygon[(i + 1) % count], point))
            {
                return true;
            }
        }

        // Half-open rule (an edge counts when exactly one endpoint is above the ray), so a ray through a vertex counts once.
        var inside = false;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var current = polygon[i];
            var previous = polygon[j];
            if ((current.Y > point.Y) != (previous.Y > point.Y))
            {
                var crossingX = current.X + ((previous.X - current.X) * (point.Y - current.Y) / (previous.Y - current.Y));
                if (point.X < crossingX)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// The area-weighted centroid (centre of mass of the enclosed area), where a room's label goes. For points enclosing
    /// no area (fewer than 3, or all on a line) it is the average of the points.
    /// </summary>
    /// <exception cref="ArgumentException">The polygon has no points.</exception>
    public static PointCm Centroid(IReadOnlyList<PointCm> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count == 0)
        {
            throw new ArgumentException("A centroid needs at least one point.", nameof(polygon));
        }

        var doubleArea = polygon.Count < 3 ? 0 : SignedDoubleArea(polygon);
        var (minX, minY, maxX, maxY) = Bounds(polygon);
        if (Math.Abs(doubleArea) <= RelativeEpsilon * (maxX - minX) * (maxY - minY))
        {
            return new PointCm(polygon.Average(p => p.X), polygon.Average(p => p.Y));
        }

        // Relative to the first vertex, for precision with large coordinates.
        var origin = polygon[0];
        double sumX = 0;
        double sumY = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (x1, y1) = (polygon[i].X - origin.X, polygon[i].Y - origin.Y);
            var next = polygon[(i + 1) % polygon.Count];
            var (x2, y2) = (next.X - origin.X, next.Y - origin.Y);
            var cross = (x1 * y2) - (x2 * y1);
            sumX += (x1 + x2) * cross;
            sumY += (y1 + y2) * cross;
        }

        return new PointCm(origin.X + (sumX / (3 * doubleArea)), origin.Y + (sumY / (3 * doubleArea)));
    }

    /// <summary>
    /// True when the two polygons share at least one point: their edges cross or touch (rooms sharing a wall intersect),
    /// or one lies inside the other. Always false when either has fewer than 3 points.
    /// </summary>
    public static bool Intersects(IReadOnlyList<PointCm> a, IReadOnlyList<PointCm> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count < 3 || b.Count < 3)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            var a1 = a[i];
            var a2 = a[(i + 1) % a.Count];
            for (var j = 0; j < b.Count; j++)
            {
                if (SegmentsIntersect(a1, a2, b[j], b[(j + 1) % b.Count]))
                {
                    return true;
                }
            }
        }

        // No edges meet: either one is entirely inside the other, or they are apart.
        return Contains(a, b[0]) || Contains(b, a[0]);
    }

    /// <summary>Twice the signed area (triangle fan from the first vertex).</summary>
    private static double SignedDoubleArea(IReadOnlyList<PointCm> polygon)
    {
        var origin = polygon[0];
        double sum = 0;
        for (var i = 1; i < polygon.Count - 1; i++)
        {
            sum += Cross(origin, polygon[i], polygon[i + 1]);
        }

        return sum;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<PointCm> polygon) =>
        (polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X), polygon.Max(p => p.Y));

    /// <summary>The z component of (b − a) × (c − a): positive, negative or zero by turn direction.</summary>
    private static double Cross(PointCm a, PointCm b, PointCm c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    /// <summary>The turn a → b → c: 1, −1, or 0 when (within tolerance) collinear.</summary>
    private static int Orientation(PointCm a, PointCm b, PointCm c)
    {
        var cross = Cross(a, b, c);
        var scale = Length(a, b) * Length(a, c);
        return Math.Abs(cross) <= RelativeEpsilon * scale ? 0 : Math.Sign(cross);
    }

    private static double Length(PointCm a, PointCm b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>Whether <paramref name="point"/> lies on the closed segment a–b.</summary>
    private static bool IsOnSegment(PointCm a, PointCm b, PointCm point) =>
        Orientation(a, b, point) == 0 && IsWithinBox(a, b, point);

    /// <summary>For a point collinear with a–b: whether it is between them (tolerance relative to the segment length).</summary>
    private static bool IsWithinBox(PointCm a, PointCm b, PointCm point)
    {
        var tolerance = RelativeEpsilon * (1 + Length(a, b));
        return point.X >= Math.Min(a.X, b.X) - tolerance && point.X <= Math.Max(a.X, b.X) + tolerance
            && point.Y >= Math.Min(a.Y, b.Y) - tolerance && point.Y <= Math.Max(a.Y, b.Y) + tolerance;
    }

    /// <summary>Whether the closed segments p1–p2 and q1–q2 share any point (crossing, touching or overlapping).</summary>
    private static bool SegmentsIntersect(PointCm p1, PointCm p2, PointCm q1, PointCm q2)
    {
        var d1 = Orientation(q1, q2, p1);
        var d2 = Orientation(q1, q2, p2);
        var d3 = Orientation(p1, p2, q1);
        var d4 = Orientation(p1, p2, q2);

        if (d1 * d2 < 0 && d3 * d4 < 0)
        {
            return true;
        }

        return (d1 == 0 && IsWithinBox(q1, q2, p1))
            || (d2 == 0 && IsWithinBox(q1, q2, p2))
            || (d3 == 0 && IsWithinBox(p1, p2, q1))
            || (d4 == 0 && IsWithinBox(p1, p2, q2));
    }

    /// <summary>
    /// For consecutive edges <paramref name="previous"/>→<paramref name="shared"/>→<paramref name="next"/>: whether the
    /// second runs back along the first (collinear, same direction from the shared vertex), i.e. they overlap.
    /// </summary>
    private static bool FoldsBack(PointCm previous, PointCm shared, PointCm next)
    {
        if (Orientation(shared, previous, next) != 0)
        {
            return false;
        }

        var dot = ((previous.X - shared.X) * (next.X - shared.X)) + ((previous.Y - shared.Y) * (next.Y - shared.Y));
        return dot > 0;
    }
}
