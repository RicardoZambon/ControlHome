using ControlHome.Domain.Geometry;

namespace ControlHome.Domain.Tests.Geometry;

public sealed class PolygonMathTests
{
    /// <summary>A 100 × 100 cm square at the origin (clockwise in screen coordinates: y grows downwards).</summary>
    private static readonly PointCm[] _square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];

    /// <summary>
    /// An L: the 100 cm wide left column (0..100 × 0..200) plus the bottom-right block (100..200 × 100..200).
    /// Area 30 000 cm²; the top-right block (100..200 × 0..100) is missing.
    /// </summary>
    private static readonly PointCm[] _lShape = [new(0, 0), new(100, 0), new(100, 100), new(200, 100), new(200, 200), new(0, 200)];

    /// <summary>A U opening upwards: two 100 cm arms and a 100 cm base, the notch is 100..200 × 0..200.</summary>
    private static readonly PointCm[] _uShape =
        [new(0, 0), new(100, 0), new(100, 200), new(200, 200), new(200, 0), new(300, 0), new(300, 300), new(0, 300)];

    /// <summary>Two triangles meeting in the middle: edges 0 and 2 cross at (50, 50).</summary>
    private static readonly PointCm[] _bowTie = [new(0, 0), new(100, 100), new(100, 0), new(0, 100)];

    private static PointCm[] Square(double x, double y, double size) =>
        [new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size)];

    // --- AreaCm2 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Area_of_a_square()
    {
        PolygonMath.AreaCm2(_square).ShouldBe(10_000, tolerance: 1e-9);
    }

    [Fact]
    public void Area_does_not_depend_on_winding()
    {
        PolygonMath.AreaCm2(_square.Reverse().ToArray()).ShouldBe(10_000, tolerance: 1e-9);
    }

    [Fact]
    public void Area_of_a_concave_polygon()
    {
        PolygonMath.AreaCm2(_lShape).ShouldBe(30_000, tolerance: 1e-9);
        PolygonMath.AreaCm2(_uShape).ShouldBe(70_000, tolerance: 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Area_of_fewer_than_three_points_is_zero(int count)
    {
        PolygonMath.AreaCm2(_square.Take(count).ToArray()).ShouldBe(0);
    }

    // --- IsSimple -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Square_triangle_and_concave_polygons_are_simple()
    {
        PolygonMath.IsSimple(_square).ShouldBeTrue();
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(50, 80)]).ShouldBeTrue();
        PolygonMath.IsSimple(_lShape).ShouldBeTrue();
        PolygonMath.IsSimple(_uShape).ShouldBeTrue();
        PolygonMath.IsSimple(_square.Reverse().ToArray()).ShouldBeTrue();
    }

    [Fact]
    public void Bow_tie_is_not_simple()
    {
        PolygonMath.IsSimple(_bowTie).ShouldBeFalse();
    }

    [Fact]
    public void Fewer_than_three_distinct_points_is_not_simple()
    {
        PolygonMath.IsSimple([]).ShouldBeFalse();
        PolygonMath.IsSimple([new(0, 0), new(100, 0)]).ShouldBeFalse();
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(0, 0)]).ShouldBeFalse();
        PolygonMath.IsSimple([new(5, 5), new(5, 5), new(5, 5), new(5, 5)]).ShouldBeFalse();
    }

    [Fact]
    public void Collinear_points_are_not_simple()
    {
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(200, 0)]).ShouldBeFalse();
        PolygonMath.IsSimple([new(0, 0), new(50, 50), new(100, 100), new(25, 25)]).ShouldBeFalse();
    }

    [Fact]
    public void Repeated_vertex_is_not_simple()
    {
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(100, 0), new(100, 100), new(0, 100)]).ShouldBeFalse();
    }

    [Fact]
    public void Closing_point_equal_to_the_first_is_not_simple()
    {
        PolygonMath.IsSimple([.. _square, new(0, 0)]).ShouldBeFalse();
    }

    [Fact]
    public void Vertex_touching_another_edge_is_not_simple()
    {
        // The vertex (50, 0) lies on the edge (0, 0)-(100, 0): the polygon pinches itself there.
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(100, 100), new(50, 0), new(0, 100)]).ShouldBeFalse();
    }

    [Fact]
    public void Edge_folding_back_over_the_previous_one_is_not_simple()
    {
        PolygonMath.IsSimple([new(0, 0), new(200, 0), new(100, 0), new(100, 100)]).ShouldBeFalse();
    }

    [Fact]
    public void Two_squares_joined_at_a_corner_are_not_simple()
    {
        PolygonMath.IsSimple([new(0, 0), new(100, 0), new(100, 100), new(200, 100), new(200, 200), new(100, 200), new(100, 100), new(0, 100)])
            .ShouldBeFalse();
    }

    // --- Contains -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Point_inside_and_outside_a_square()
    {
        PolygonMath.Contains(_square, new(50, 50)).ShouldBeTrue();
        PolygonMath.Contains(_square, new(150, 50)).ShouldBeFalse();
        PolygonMath.Contains(_square, new(-0.001, 50)).ShouldBeFalse();
        PolygonMath.Contains(_square, new(50, 100.001)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(50, 0)]
    [InlineData(100, 50)]
    [InlineData(50, 100)]
    [InlineData(0, 50)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void Point_on_the_boundary_counts_as_inside(double x, double y)
    {
        PolygonMath.Contains(_square, new(x, y)).ShouldBeTrue();
    }

    [Fact]
    public void Point_on_a_diagonal_edge_counts_as_inside()
    {
        PointCm[] triangle = [new(0, 0), new(300, 0), new(0, 300)];

        PolygonMath.Contains(triangle, new(100, 200)).ShouldBeTrue();
        PolygonMath.Contains(triangle, new(100.1, 200)).ShouldBeFalse();
    }

    [Fact]
    public void Concave_polygon_containment()
    {
        PolygonMath.Contains(_uShape, new(50, 100)).ShouldBeTrue();   // left arm
        PolygonMath.Contains(_uShape, new(250, 100)).ShouldBeTrue();  // right arm
        PolygonMath.Contains(_uShape, new(150, 250)).ShouldBeTrue();  // base
        PolygonMath.Contains(_uShape, new(150, 100)).ShouldBeFalse(); // the notch
        PolygonMath.Contains(_uShape, new(150, 200)).ShouldBeTrue();  // the notch's floor is boundary

        PolygonMath.Contains(_lShape, new(150, 50)).ShouldBeFalse();  // the missing corner
        PolygonMath.Contains(_lShape, new(150, 150)).ShouldBeTrue();
    }

    [Fact]
    public void Ray_through_a_vertex_is_counted_once()
    {
        // A diamond: the horizontal ray from (10, 50) passes exactly through the vertices (0, 50) and (100, 50).
        PointCm[] diamond = [new(50, 0), new(100, 50), new(50, 100), new(0, 50)];

        PolygonMath.Contains(diamond, new(10, 50)).ShouldBeTrue();
        PolygonMath.Contains(diamond, new(-10, 50)).ShouldBeFalse();
        PolygonMath.Contains(diamond, new(110, 50)).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_is_inside_fewer_than_three_points()
    {
        PolygonMath.Contains([new(0, 0), new(100, 0)], new(50, 0)).ShouldBeFalse();
    }

    // --- Centroid -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Centroid_of_a_square()
    {
        var centroid = PolygonMath.Centroid(Square(100, 200, 50));

        centroid.X.ShouldBe(125, tolerance: 1e-9);
        centroid.Y.ShouldBe(225, tolerance: 1e-9);
    }

    [Fact]
    public void Centroid_of_an_L_shape_is_area_weighted()
    {
        // Column 0..100 × 0..200 (area 20 000, centre (50, 100)) + block 100..200 × 100..200 (area 10 000, centre (150, 150)).
        var centroid = PolygonMath.Centroid(_lShape);

        centroid.X.ShouldBe(250.0 / 3, tolerance: 1e-9);
        centroid.Y.ShouldBe(350.0 / 3, tolerance: 1e-9);
    }

    [Fact]
    public void Centroid_does_not_depend_on_winding()
    {
        var centroid = PolygonMath.Centroid(_lShape.Reverse().ToArray());

        centroid.X.ShouldBe(250.0 / 3, tolerance: 1e-9);
        centroid.Y.ShouldBe(350.0 / 3, tolerance: 1e-9);
    }

    [Fact]
    public void Centroid_of_degenerate_points_is_their_average()
    {
        PolygonMath.Centroid([new(0, 0), new(100, 0), new(200, 0)]).ShouldBe(new PointCm(100, 0));
        PolygonMath.Centroid([new(10, 20)]).ShouldBe(new PointCm(10, 20));
    }

    [Fact]
    public void Centroid_of_no_points_throws()
    {
        Should.Throw<ArgumentException>(() => PolygonMath.Centroid([]));
    }

    // --- Intersects ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Overlapping_squares_intersect()
    {
        PolygonMath.Intersects(Square(0, 0, 100), Square(50, 50, 100)).ShouldBeTrue();
    }

    [Fact]
    public void Disjoint_squares_do_not_intersect()
    {
        PolygonMath.Intersects(Square(0, 0, 100), Square(200, 0, 100)).ShouldBeFalse();
        PolygonMath.Intersects(Square(0, 0, 100), Square(100.5, 0, 100)).ShouldBeFalse();
    }

    [Fact]
    public void Nested_polygons_intersect_both_ways()
    {
        PolygonMath.Intersects(Square(0, 0, 300), Square(100, 100, 50)).ShouldBeTrue();
        PolygonMath.Intersects(Square(100, 100, 50), Square(0, 0, 300)).ShouldBeTrue();
    }

    [Fact]
    public void Squares_sharing_a_wall_intersect()
    {
        PolygonMath.Intersects(Square(0, 0, 100), Square(100, 0, 100)).ShouldBeTrue();
    }

    [Fact]
    public void Crossing_bars_intersect_without_containing_a_vertex()
    {
        PointCm[] horizontal = [new(0, 40), new(300, 40), new(300, 60), new(0, 60)];
        PointCm[] vertical = [new(140, 0), new(160, 0), new(160, 300), new(140, 300)];

        PolygonMath.Intersects(horizontal, vertical).ShouldBeTrue();
    }

    [Fact]
    public void Polygon_in_the_notch_of_a_concave_one_does_not_intersect()
    {
        PolygonMath.Intersects(_uShape, Square(120, 20, 50)).ShouldBeFalse();
    }
}
