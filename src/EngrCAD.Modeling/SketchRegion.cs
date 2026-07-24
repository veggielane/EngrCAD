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

    /// <param name="forRevolution">
    /// When the region is destined for <c>Sdf.RevolvedRegion</c>, boundary segments
    /// lying on the axis (x = 0) are excluded from the *distance* — the axis is
    /// interior to the solid of revolution, not a surface. Parity is unaffected: a +x
    /// ray from any r ≥ 0 query never crosses x = 0.
    /// </param>
    public SketchRegion(Sketch sketch, bool forRevolution = false)
    {
        Collect(sketch, forRevolution);
        foreach (var hole in sketch.Holes)
            Collect(hole, forRevolution);
        Bounds = sketch.Bounds;
    }

    private void Collect(Sketch sketch, bool forRevolution)
    {
        foreach (var segment in sketch.Segments)
        {
            // Weld-scale (1e-9 = Tolerance.Default.Linear) on-axis classification —
            // must agree with RevolveFullTurn's pole detection so all representations
            // drop the same on-axis stretches.
            bool onAxis = forRevolution
                && Math.Abs(segment.Start.X) <= 1e-9
                && Math.Abs(segment.End.X) <= 1e-9
                && segment.Bounds().Max.X <= 1e-9;
            if (!onAxis)
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
