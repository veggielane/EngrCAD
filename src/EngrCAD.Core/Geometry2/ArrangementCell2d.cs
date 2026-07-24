namespace EngrCAD.Core.Geometry2;

/// <summary>
/// One bounded cell of an <see cref="Arrangement2d"/>: a counter-clockwise outer polygon
/// plus zero or more clockwise hole loops (island components floating inside the cell).
/// Loops are available both as vertex indices into the owning arrangement and as
/// materialized points; consecutive duplicate points do not occur, but a loop may double
/// back along an edge that dangles into the cell (a zero-width slit).
/// </summary>
public sealed class ArrangementCell2d
{
    /// <summary>Counter-clockwise outer boundary, as vertex indices into the arrangement.</summary>
    public IReadOnlyList<int> OuterVertices { get; }

    /// <summary>Counter-clockwise outer boundary points.</summary>
    public IReadOnlyList<Vector2d> Outer { get; }

    /// <summary>Clockwise hole loops, as vertex indices into the arrangement.</summary>
    public IReadOnlyList<IReadOnlyList<int>> HoleVertices { get; }

    /// <summary>Clockwise hole loop points.</summary>
    public IReadOnlyList<IReadOnlyList<Vector2d>> Holes { get; }

    /// <summary>Net area of the cell: outer loop area minus the holes.</summary>
    public double Area { get; }

    internal ArrangementCell2d(
        IReadOnlyList<int> outerVertices,
        IReadOnlyList<Vector2d> outer,
        IReadOnlyList<IReadOnlyList<int>> holeVertices,
        IReadOnlyList<IReadOnlyList<Vector2d>> holes,
        double area)
    {
        OuterVertices = outerVertices;
        Outer = outer;
        HoleVertices = holeVertices;
        Holes = holes;
        Area = area;
    }
}
