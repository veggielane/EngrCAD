using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// Extract-modify-reinsert. The invariant under test throughout is that the seam survives:
/// a region put back must join the rest of the model with no crack, and a replacement that
/// would leave one must be refused rather than welded approximately.
/// </summary>
public class MeshRegionOperatorTests
{
    private static HalfEdgeMesh Box() =>
        MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2))).Triangulated();

    /// <summary>The faces whose vertices all sit on the plane z = 2 — the box's top.</summary>
    private static MeshFaceSelection Top(HalfEdgeMesh mesh) => MeshFaceSelection.FromIndices(
        mesh, mesh.Faces.Where(f => f.Vertices().All(v => v.Position.Z == 2)).Select(f => f.Index));

    [Fact]
    public void Extract_TakesTheRegionAndRecordsWhereItCameFrom()
    {
        var mesh = Box();
        var operatorSession = MeshRegionOperator.Extract(mesh, Top(mesh));

        Assert.Equal(2, operatorSession.Region.FaceCount);      // two triangles of the top square
        Assert.Equal(4.0, operatorSession.Region.SurfaceArea(), 12);
        Assert.False(operatorSession.Region.IsClosed);          // open along the seam
        Assert.Equal(4, operatorSession.SeamEdges.Count);       // the square rim

        // The map points back into the base mesh, and the positions agree.
        for (int v = 0; v < operatorSession.Region.VertexCount; v++)
        {
            Assert.Equal(
                mesh.GetPosition(operatorSession.RegionToBaseVertex[v]),
                operatorSession.Region.GetPosition(v));
        }
        Assert.Same(mesh, operatorSession.Base);
    }

    [Fact]
    public void Reinsert_UnchangedRegion_ReproducesTheOriginalSolid()
    {
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));

        var result = session.Reinsert(session.Region).Base;

        result.Validate();
        Assert.True(result.IsClosed, "putting a region back unchanged must not open the mesh");
        Assert.Equal(mesh.FaceCount, result.FaceCount);
        Assert.Equal(mesh.SignedVolume(), result.SignedVolume(), 12);
        Assert.Equal(2, result.EulerCharacteristic);
    }

    /// <summary>A sphere and its polar cap — a region with genuine interior vertices.</summary>
    private static (HalfEdgeMesh Mesh, MeshFaceSelection Cap) SphereCap()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16).Triangulated();
        var cap = MeshFaceSelection.FromIndices(
            sphere, sphere.Faces.Where(f => f.Centroid().Z > 0.5).Select(f => f.Index));
        return (sphere, cap);
    }

    [Fact]
    public void Reinsert_InteriorVerticesMovedFreely_KeepsTheSolidClosed()
    {
        // The motivating case: reshape one patch and nothing else. Everything strictly
        // inside the seam is the caller's to do as they like.
        var (sphere, cap) = SphereCap();
        var session = MeshRegionOperator.Extract(sphere, cap);
        var seam = session.Region.BoundaryLoops().SelectMany(l => l).Select(h => h.Origin.Index).ToHashSet();

        var (positions, faces) = session.Region.ToIndexed();
        for (int v = 0; v < positions.Length; v++)
        {
            if (!seam.Contains(v))
                positions[v] *= 1.2; // push the dome out
        }
        var domed = HalfEdgeMesh.Build(positions, faces);

        var result = session.Reinsert(domed);

        result.Base.Validate();
        Assert.True(result.Base.IsClosed);
        Assert.Equal(2, result.Base.EulerCharacteristic);
        Assert.Equal(sphere.FaceCount, result.Base.FaceCount);
        Assert.True(result.Base.SignedVolume() > sphere.SignedVolume(), "the dome should add volume");
        Assert.Equal(cap.Count, result.Selection.Count);
    }

    [Fact]
    public void Reinsert_DecimatedRegion_ReplacesOnlyThatPatch()
    {
        // MeshDecimator preserves boundaries exactly, which is precisely the contract a
        // region edit has to satisfy — so decimating one face group just works.
        var (sphere, cap) = SphereCap();
        var session = MeshRegionOperator.Extract(sphere, cap);

        var coarse = MeshDecimator.Decimate(session.Region, session.Region.FaceCount / 2);
        Assert.True(coarse.FaceCount < session.Region.FaceCount);

        var result = session.Reinsert(coarse);

        result.Base.Validate();
        Assert.True(result.Base.IsClosed);
        Assert.Equal(2, result.Base.EulerCharacteristic);
        Assert.Equal(sphere.FaceCount - cap.Count + coarse.FaceCount, result.Base.FaceCount);
    }

    [Fact]
    public void Reinsert_ReturnsASessionThatCanBeEditedAgain()
    {
        var (sphere, cap) = SphereCap();
        var session = MeshRegionOperator.Extract(sphere, cap);

        // Two rounds of decimation, the second starting from the first's output.
        session = session.Reinsert(MeshDecimator.Decimate(session.Region, session.Region.FaceCount * 3 / 4));
        int afterFirst = session.Region.FaceCount;
        session = session.Reinsert(MeshDecimator.Decimate(session.Region, session.Region.FaceCount * 3 / 4));

        session.Base.Validate();
        Assert.True(session.Base.IsClosed);
        Assert.True(session.Region.FaceCount < afterFirst);
    }

    [Fact]
    public void Reinsert_RefusesASubdivisionThatMovedTheSeam()
    {
        // Loop's default Warren boundary rule SMOOTHS the open boundary, so the rim comes
        // back as a different curve near the old one rather than a subdivision of it. That
        // is still refused, and must be: a moved rim welds into an invisible crack instead
        // of failing. What is now accepted is the same subdivision with the rim pinned.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));

        var error = Assert.Throws<ArgumentException>(
            () => session.Reinsert(LoopSubdivision.Subdivide(session.Region, 1)));

        Assert.Contains("boundary", error.Message);
    }

    [Fact]
    public void Reinsert_CarriesASubdividedSeamIntoTheNeighbours()
    {
        // The larger operation: the replacement split every seam edge in two, so every base
        // face using one gains the new vertex — a T-junction would be an open shell.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));
        var refined = LoopSubdivision.Subdivide(session.Region, 1, preserveBoundary: true);
        Assert.Equal(8, refined.FaceCount); // two triangles → eight

        var result = session.Reinsert(refined);

        result.Base.Validate();
        Assert.True(result.Base.IsClosed, "a carried seam split must not leave a T-junction");
        Assert.Equal(2, result.Base.EulerCharacteristic);
        // The top is planar, so subdividing it changes nothing geometrically.
        Assert.Equal(8.0, result.Base.SignedVolume(), 12);
        Assert.Equal(24.0, result.Base.SurfaceArea(), 12);
        // Four rim midpoints are new, and each of the four side triangles using a rim edge
        // was re-fanned into two.
        Assert.Equal(mesh.VertexCount + refined.VertexCount - session.Region.VertexCount,
            result.Base.VertexCount);
        Assert.Equal(refined.FaceCount, result.Selection.Count);
    }

    [Fact]
    public void Reinsert_SubdividedSeam_ChainsAndStaysManifold()
    {
        // Twice over, on a curved region with a long rim: the second round subdivides a seam
        // that was already refined once, which is where a stale seam bookkeeping would show.
        var (sphere, cap) = SphereCap();
        var session = MeshRegionOperator.Extract(sphere, cap);
        int rim = session.SeamEdges.Count;

        session = session.Reinsert(LoopSubdivision.Subdivide(session.Region, 1, preserveBoundary: true));
        Assert.Equal(2 * rim, session.SeamEdges.Count);
        session = session.Reinsert(LoopSubdivision.Subdivide(session.Region, 1, preserveBoundary: true));

        session.Base.Validate();
        Assert.True(session.Base.IsClosed);
        Assert.Equal(2, session.Base.EulerCharacteristic);
        Assert.Equal(4 * rim, session.SeamEdges.Count);
    }

    [Fact]
    public void Reinsert_RefinedSeam_WeldsTheNewVertexRatherThanDuplicatingIt()
    {
        // The crack that a naive implementation would produce: the base side and the
        // replacement side each create their own vertex at the split point, positions equal,
        // indices different. Build() would then report a boundary edge on both — so a closed
        // result IS the assertion, but the vertex count pins it directly.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));
        var refined = LoopSubdivision.Subdivide(session.Region, 1, preserveBoundary: true);

        var result = session.Reinsert(refined).Base;

        var midpoint = new Vector3d(1, 0, 2); // the split point of one rim edge
        Assert.Equal(1, Enumerable.Range(0, result.VertexCount)
            .Count(v => result.GetPosition(v) == midpoint));
        Assert.Empty(result.BoundaryLoops());
    }

    [Fact]
    public void Reinsert_ReplacementWithDifferentGeometryInsideTheSeam()
    {
        // A genuinely different region: the top square replaced by a four-triangle fan
        // through a raised apex — a boss, built by hand, welded in through the same rim.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));

        var corners = new Vector3d[] { (0, 0, 2), (2, 0, 2), (2, 2, 2), (0, 2, 2) };
        var positions = new List<Vector3d>(corners) { (1, 1, 3) };
        var rim = OrientedRim(session, corners);
        var faces = new List<int[]>();
        for (int i = 0; i < 4; i++)
            faces.Add([rim[i], rim[(i + 1) % 4], 4]);
        var replacement = HalfEdgeMesh.Build(positions, faces);

        var result = session.Reinsert(replacement).Base;

        result.Validate();
        Assert.True(result.IsClosed);
        Assert.Equal(2, result.EulerCharacteristic);
        // Box plus a pyramid of base 2×2 and height 1.
        Assert.Equal(8 + 4.0 / 3, result.SignedVolume(), 12);
    }

    [Fact]
    public void Reinsert_RefusesAReplacementWhoseBoundaryMoved()
    {
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));

        // One rim vertex nudged by a picometre — far below any tolerance in the codebase,
        // and exactly the kind of drift that would weld into an invisible crack.
        var (basePositions, faces) = session.Region.ToIndexed();
        for (int i = 0; i < basePositions.Length; i++)
        {
            if (basePositions[i] == new Vector3d(0, 0, 2))
                basePositions[i] = (1e-12, 0, 2);
        }
        var drifted = HalfEdgeMesh.Build(basePositions, faces);

        var error = Assert.Throws<ArgumentException>(() => session.Reinsert(drifted));

        Assert.Contains("boundary", error.Message);
        Assert.Contains("crack", error.Message);
    }

    [Fact]
    public void Reinsert_RefusesAReplacementMissingPartOfTheBoundary()
    {
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));

        // Half the top: its boundary is only half the rim, plus a new diagonal.
        var (positions, faces) = session.Region.ToIndexed();
        var half = HalfEdgeMesh.Build(positions, faces.Take(1));

        var error = Assert.Throws<ArgumentException>(() => session.Reinsert(half));

        Assert.Contains("boundary", error.Message);
    }

    [Fact]
    public void Reinsert_RefusesAReplacementWoundTheOtherWay()
    {
        // Reversed winding shows up as reversed boundary edges, so it is caught by the seam
        // check rather than by producing a mesh that is closed but inside out.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));
        var (positions, faces) = session.Region.ToIndexed();
        var flipped = HalfEdgeMesh.Build(positions, faces.Select(f => (IReadOnlyList<int>)[.. f.Reverse()]));

        Assert.Throws<ArgumentException>(() => session.Reinsert(flipped));
    }

    [Fact]
    public void TheOriginalMeshIsNeverTouched_EvenWhenReinsertionFails()
    {
        // The transactional guarantee, and the reason this needs no change-set journal:
        // HalfEdgeMesh is immutable, so a refusal leaves the caller holding the original.
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, Top(mesh));
        double volume = mesh.SignedVolume();
        int faces = mesh.FaceCount;

        var (positions, loops) = session.Region.ToIndexed();
        Assert.Throws<ArgumentException>(() => session.Reinsert(HalfEdgeMesh.Build(positions, loops.Take(1))));

        Assert.Equal(faces, mesh.FaceCount);
        Assert.Equal(volume, mesh.SignedVolume(), 12);
        mesh.Validate();
    }

    [Fact]
    public void WholeMeshRegion_HasNoSeamAndAcceptsAnyClosedReplacement()
    {
        var mesh = Box();
        var session = MeshRegionOperator.Extract(mesh, MeshFaceSelection.All(mesh));

        Assert.Empty(session.SeamEdges);
        Assert.True(session.Region.IsClosed);

        var sphere = MeshPrimitives.UvSphere(1.0, 16, 8).Triangulated();
        var result = session.Reinsert(sphere).Base;

        result.Validate();
        Assert.Equal(sphere.SignedVolume(), result.SignedVolume(), 12);
    }

    [Fact]
    public void SelectionFromAnotherMesh_IsRejected()
    {
        var mesh = Box();
        var other = Box();

        Assert.Throws<ArgumentException>(() => MeshRegionOperator.Extract(mesh, Top(other)));
    }

    /// <summary>
    /// Indices of <paramref name="corners"/> in a hand-built replacement, ordered so the
    /// fan built from them walks the rim the same way the region did.
    /// </summary>
    private static int[] OrientedRim(MeshRegionOperator session, IReadOnlyList<Vector3d> corners)
    {
        // The seam runs with the region on the left; a fan face (rim[i], rim[i+1], apex)
        // therefore has to traverse the rim in the same direction as the seam edges do.
        var next = session.SeamEdges.ToDictionary(
            e => session.Base.GetPosition(e.From), e => session.Base.GetPosition(e.To));
        var order = new List<int> { 0 };
        var current = corners[0];
        for (int i = 1; i < corners.Count; i++)
        {
            current = next[current];
            order.Add(corners.ToList().IndexOf(current));
        }
        return [.. order];
    }
}
