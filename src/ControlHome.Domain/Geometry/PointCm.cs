namespace ControlHome.Domain.Geometry;

/// <summary>
/// A point on a floor plan, in centimetres. The origin is the floor's top-left corner and y grows downwards (SVG
/// coordinates), so a floor's area is <c>0..WidthCm × 0..HeightCm</c>.
/// </summary>
public sealed record PointCm(double X, double Y);
