using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>The tool holder as the collision check sees it: a flat disc of
/// <paramref name="Diameter"/> whose bottom face rides <paramref name="StickoutLength"/>
/// above the tool tip. Real holders taper; the disc is the CONSERVATIVE envelope of
/// everything at or above the holder nose, so a pass the check clears is clear of any
/// holder that fits inside the disc.</summary>
public sealed record ToolHolder(double Diameter, double StickoutLength)
{
    /// <summary>Half the diameter.</summary>
    public double Radius => Diameter / 2;

    /// <summary>Refuses an unusable holder by name. A holder no wider than its cutter is
    /// refused too — such a disc cannot collide before the cutter's own flank engages, so
    /// the check would be vacuous, and the number that matters there (the flute length
    /// against the cut depth) is not modelled here.</summary>
    public void Validate(double cutterDiameter)
    {
        if (!(Diameter > 0) || !double.IsFinite(Diameter))
            throw new ArgumentException(
                $"Holder diameter must be finite and positive; got {Diameter:0.###}.");
        if (!(StickoutLength > 0) || !double.IsFinite(StickoutLength))
            throw new ArgumentException(
                $"StickoutLength must be finite and positive; got {StickoutLength:0.###}.");
        if (Diameter <= cutterDiameter)
            throw new ArgumentException(
                $"A holder of Ø{Diameter:0.###} is no wider than its Ø{cutterDiameter:0.###} "
                + "cutter, so it cannot collide before the cutter's own flank engages and the "
                + "check would be vacuous. The number to verify there is the flute length "
                + "against the cut depth, which this check does not model.");
    }
}

/// <summary>One holder collision: the pass point whose holder bottom sits below what the
/// surface under the disc requires, with the deficit (how far INTO the part the disc
/// reaches) and the surface height that decided it.</summary>
public sealed record HolderCollision(
    int OperationIndex, int PassIndex, Vector3d Point, double RequiredZ, double Deficit);

/// <summary>The holder check's answer: every colliding pass point, and the smallest
/// stickout that clears every point checked (`max(requiredZ − point.z)` over the passes,
/// floored at zero) — the number that turns a failing setup into a passing one.</summary>
public sealed record HolderReport(
    IReadOnlyList<HolderCollision> Collisions, double MinimumStickout, int PointsChecked)
{
    /// <summary>True when no pass point puts the holder into the part.</summary>
    public bool Ok => Collisions.Count == 0;
}

/// <summary>
/// Holder collision over finished geometry — the surfacing residual. The holder is modelled
/// as a flat disc riding <see cref="ToolHolder.StickoutLength"/> above the tip, so a pass
/// point collides exactly when the surface under the disc reaches above the holder's bottom
/// — which is the FLAT drop-cutter question at the holder's own radius, answered by the same
/// vertex/edge/face contact arithmetic the flat cutter rides (<see cref="DropCutter"/>), so
/// the holder check and the flat cutter cannot disagree about what a disc touches.
///
/// <para><b>Checked against the FINISHED part, stated rather than hidden</b>: the in-process
/// stock is more material than the finished body, so a roughing pass can hit stock this
/// check cannot see — the finished check is exact for finishing passes (where holder
/// collisions live) and a lower bound for roughing, and the margin for in-process stock is
/// the caller's. <b>Zero clearance passes</b> (a holder bottom exactly at the surface is
/// resting contact, not a collision — the interference checker's own rule), so a stickout
/// equal to <see cref="HolderReport.MinimumStickout"/> clears.</para>
/// </summary>
public static class CncHolder
{
    /// <summary>Checks one operation's passes; see <see cref="Check(Shape, IReadOnlyList{MillOperation}, ToolHolder)"/>.</summary>
    public static HolderReport Check(Shape shape, MillOperation operation, ToolHolder holder) =>
        Check(shape, [operation], holder);

    /// <summary>Checks every pass point of every operation against the holder disc over the
    /// shape's finished surface. All operations must share one tool diameter (the holder is
    /// one physical object); the report carries every collision and the smallest stickout
    /// that clears them all.</summary>
    public static HolderReport Check(
        Shape shape, IReadOnlyList<MillOperation> operations, ToolHolder holder)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(holder);
        if (operations.Count == 0)
            throw new ArgumentException("At least one operation is required.");
        double cutterDiameter = operations[0].Tool.Diameter;
        foreach (var op in operations)
            if (op.Tool.Diameter != cutterDiameter)
                throw new ArgumentException(
                    "All operations must share one tool diameter — a holder is one physical "
                    + $"object; got Ø{cutterDiameter:0.###} and Ø{op.Tool.Diameter:0.###}.");
        holder.Validate(cutterDiameter);

        var mesh = shape.ToMesh().Triangulated();
        var bounds = shape.Bounds();
        var probe = new DropProbe(mesh, holder.Radius, bounds.Min.Z);
        var disc = MillCutter.FlatEnd(holder.Diameter);

        var collisions = new List<HolderCollision>();
        double minimumStickout = 0;
        int points = 0;
        for (int o = 0; o < operations.Count; o++)
        {
            var passes = operations[o].Passes;
            for (int p = 0; p < passes.Count; p++)
            {
                foreach (var point in passes[p].Points)
                {
                    points++;
                    double required = probe.TipAt(point.X, point.Y, disc);
                    minimumStickout = Math.Max(minimumStickout, required - point.Z);
                    double bottom = point.Z + holder.StickoutLength;
                    if (bottom < required)
                        collisions.Add(new HolderCollision(
                            o, p, point, required, required - bottom));
                }
            }
        }
        return new HolderReport(collisions, minimumStickout, points);
    }
}
