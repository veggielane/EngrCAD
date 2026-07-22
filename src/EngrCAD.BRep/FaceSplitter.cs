using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Result of splitting a face by a closed curve: the face keeps its loops and
/// gains the curve as a hole; the disk is the piece inside the curve (null when the
/// caller opted out to hand the edge's second use to another face, e.g. a drilled bore).</summary>
public sealed record ClosedSplitResult(BrepFace FaceWithHole, BrepFace? Disk, BrepEdge Edge);

/// <summary>
/// Face splitting along intersection curves. Current scope: closed curves lying strictly
/// in a face's interior (the drilled-hole / boss case). Open curves crossing the face
/// boundary — full arrangement splitting — are the next step toward B-Rep booleans.
/// </summary>
public static class FaceSplitter
{
    /// <summary>
    /// Splits a face along a closed curve lying in its interior. The original face's
    /// loops are kept and the curve becomes an inner (hole) loop wound opposite the outer
    /// loop; the disk face carries the curve as its outer loop. Both share one new edge,
    /// keeping the result two-manifold.
    /// </summary>
    public static ClosedSplitResult SplitByClosedCurve(BrepFace face, Curve3d closedCurve, bool createDisk = true)
    {
        if (!closedCurve.IsClosed)
            throw new ArgumentException("The splitting curve must be closed.", nameof(closedCurve));

        var pulled = FaceGeometry.PullCurve(closedCurve, face.Surface);
        var probe = closedCurve.PointAt(closedCurve.Domain.Start);
        if (!FaceGeometry.Contains(face, probe))
            throw new ArgumentException("The splitting curve must lie inside the face.", nameof(closedCurve));

        bool curveCcw = FaceGeometry.LoopSignedArea(pulled) > 0;

        var seam = new BrepVertex(probe);
        var edge = new BrepEdge(closedCurve, closedCurve.Domain, seam, seam);

        // Hole loops wind opposite the (CCW) outer loop; the disk's outer loop winds CCW.
        var holeCoedge = new BrepCoedge(edge, sameSense: !curveCcw);
        var faceWithHole = new BrepFace(face.Surface, [.. face.Loops, new BrepLoop([holeCoedge])]);

        BrepFace? disk = null;
        if (createDisk)
            disk = new BrepFace(face.Surface, [new BrepLoop([new BrepCoedge(edge, sameSense: curveCcw)])]);

        return new ClosedSplitResult(faceWithHole, disk, edge);
    }
}
