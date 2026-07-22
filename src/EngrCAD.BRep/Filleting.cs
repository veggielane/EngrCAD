using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Edge filleting. Current scope: closed circular rim edges where a planar cap meets a
/// coaxial cylindrical band (cylinder rims, drilled-boss rims) — the corner-free case,
/// replaced exactly by a quarter-torus surface of revolution. General edge/vertex
/// filleting (chains with corner patches) is future work.
/// </summary>
public static class Filleting
{
    /// <summary>
    /// Fillets a closed circular edge between a planar cap and a cylindrical band with
    /// the given radius. The cap shrinks, the band shortens, and an exact quarter-torus
    /// (a revolved circular arc) joins them. Returns the new solid; untouched faces are
    /// reused (the input solid is consumed).
    /// </summary>
    public static BrepSolid FilletEdge(BrepSolid solid, BrepEdge edge, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!edge.IsClosedEdge || edge.Curve.Underlying is not Circle3d rim)
            throw new NotSupportedException("Only closed circular edges can be filleted yet.");
        if (edge.Uses.Count != 2)
            throw new ArgumentException("The edge must be interior to a solid.", nameof(edge));

        var capUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.Surface is PlaneSurface);
        var bandUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.Surface is CylinderSurface);
        if (capUse is null || bandUse is null)
            throw new NotSupportedException("Filleting expects the edge to join a planar cap and a cylindrical band.");

        var cap = (PlaneSurface)capUse.Loop.Face.Surface;
        var bandFace = bandUse.Loop.Face;
        var band = (CylinderSurface)bandFace.Surface;

        var axis = band.Axis.Normalized();
        var capNormal = cap.Normal.Normalized();
        if (!capNormal.IsParallelTo(axis, new Tolerance(1e-9, 1e-6)))
            throw new NotSupportedException("The cap must be perpendicular to the band's axis.");
        var outward = capNormal; // outward normal of the cap, ±axis

        double bigRadius = rim.Radius;
        if (radius >= bigRadius)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius must be smaller than the rim radius.");

        // Band length check: distance to the band's far ring.
        var farLoop = bandFace.Loops.First(l => !l.Coedges.Contains(bandUse));
        if (farLoop.Coedges is not [{ Edge.Curve.Underlying: Circle3d farCircle }])
            throw new NotSupportedException("The band must be bounded by two circular rings.");
        double bandLength = Math.Abs((rim.Center - farCircle.Center).Dot(axis));
        if (radius >= bandLength)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius exceeds the band length.");

        // Geometry: arc center sits radius inward both radially and axially.
        var radial = rim.XDirection;
        var around = axis.Cross(radial);
        var arcCircle = new Circle3d(
            rim.Center + radial * (bigRadius - radius) - outward * radius,
            radial, outward, radius);
        var arc = new CurveSegment(arcCircle, 0, Math.PI / 2); // band tangent → cap tangent

        var bandRing = new Circle3d(rim.Center - outward * radius, radial, around, bigRadius);
        var capRing = new Circle3d(rim.Center, radial, around, bigRadius - radius);
        var bandSeam = new BrepVertex(bandRing.PointAt(0));
        var capSeam = new BrepVertex(capRing.PointAt(0));
        var bandRingEdge = new BrepEdge(bandRing, new Interval(0, 2 * Math.PI), bandSeam, bandSeam);
        var capRingEdge = new BrepEdge(capRing, new Interval(0, 2 * Math.PI), capSeam, capSeam);

        // Sense bookkeeping: the new rings wind about +axis; the old rim wound about
        // ±axis. Preserve each face's traversal orientation, and give the torus the
        // opposite uses.
        int oldWinding = Math.Sign(rim.Axis.Dot(axis));
        bool bandSense = oldWinding > 0 ? bandUse.SameSense : !bandUse.SameSense;
        bool capSense = oldWinding > 0 ? capUse.SameSense : !capUse.SameSense;

        bandUse.Loop.ReplaceCoedge(bandUse, [new BrepCoedge(bandRingEdge, bandSense)]);
        capUse.Loop.ReplaceCoedge(capUse, [new BrepCoedge(capRingEdge, capSense)]);

        var torus = new BrepFace(
            new RevolvedSurface(arc, rim.Center, axis),
            [
                new BrepLoop([new BrepCoedge(bandRingEdge, !bandSense)]),
                new BrepLoop([new BrepCoedge(capRingEdge, !capSense)]),
            ]);

        return new BrepSolid([new BrepShell([.. solid.Faces, torus])]);
    }
}
