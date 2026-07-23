using EngrCAD.Core;
using EngrCAD.Implicit;

namespace EngrCAD.Modeling;

/// <summary>
/// A <see cref="Sketch"/> as an exact 2D signed distance field
/// (<see cref="IPlanarRegion"/>): magnitude is the distance to the nearest segment
/// (lines/arcs exact, béziers Newton-refined), sign is even–odd ray parity computed
/// from precomputed y-monotone pieces with exact crossings — holes fall out for free.
/// </summary>
public sealed class SketchRegion : IPlanarRegion
{
    private readonly List<SketchSegment> _segments = [];
    private readonly List<MonotonePiece> _pieces = [];

    public Aabb Bounds { get; }

    public SketchRegion(Sketch sketch)
    {
        Collect(sketch);
        foreach (var hole in sketch.Holes)
            Collect(hole);
        Bounds = sketch.Bounds;
    }

    private void Collect(Sketch sketch)
    {
        foreach (var segment in sketch.Segments)
        {
            _segments.Add(segment);
            _pieces.AddRange(segment.MonotonePieces());
        }
    }

    public double SignedDistance(in Vector2d point)
    {
        double distance = double.PositiveInfinity;
        foreach (var segment in _segments)
            distance = Math.Min(distance, segment.Distance(point));

        int crossings = 0;
        foreach (var piece in _pieces)
        {
            // Half-open endpoint rule on monotone pieces: robust at shared vertices.
            if (piece.Y0 > point.Y != piece.Y1 > point.Y && piece.XAtY(point.Y) > point.X)
                crossings++;
        }
        return (crossings & 1) == 1 ? -distance : distance;
    }
}
