using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The upper hole-fill tiers: <see cref="HoleFiller.FillMinimal"/> (minimum-weight
/// triangulation of the rim's own vertices) and <see cref="HoleFiller.FillSmoothed"/>
/// (remeshed, Laplacian-relaxed patch), plus their <see cref="HoleFiller.FillAll"/> dispatch.
/// </summary>
public class HoleFillerTierTests
{
    private static HalfEdgeMesh RemoveFaces(HalfEdgeMesh mesh, params int[] faceIndices)
    {
        var (positions, faces) = mesh.ToIndexed();
        var remove = new HashSet<int>(faceIndices);
        return HalfEdgeMesh.Build(positions, faces.Where((_, i) => !remove.Contains(i)).ToList());
    }

    private static int[] FacesWithNormal(HalfEdgeMesh mesh, Vector3d normal) =>
        [.. mesh.Faces.Where(f => f.Normal().Dot(normal) > 0.9).Select(f => f.Index)];

    /// <summary>
    /// A UV sphere with a bite taken out of it around a <b>tilted</b> axis. The tilt matters:
    /// a polar cap's rim sits at constant z and is therefore <i>exactly</i> planar, so the
    /// planar tier would claim it — around a tilted axis the rim misses any plane by its own
    /// tessellation's sagitta, which is what the upper tiers are for.
    /// </summary>
    private static HalfEdgeMesh SphereWithBiteRemoved(int segments, int rings, double angleDegrees = 25)
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments, rings).Triangulated();
        var axis = new Vector3d(0.3, 0.5, 0.8).Normalized();
        double limit = Math.Cos(angleDegrees * Math.PI / 180.0);
        var remove = new List<int>();
        foreach (var face in sphere.Faces)
        {
            var centroid = Vector3d.Zero;
            foreach (var v in face.Vertices())
                centroid += v.Position;
            if ((centroid / 3).Normalized().Dot(axis) > limit)
                remove.Add(face.Index);
        }
        return RemoveFaces(sphere, [.. remove]);
    }

    // ---- FillMinimal ----

    [Fact]
    public void FillMinimal_PlanarQuadHole_RestoresExactVolumeWithNoNewVertices()
    {
        var box = MeshPrimitives.Box(2, 3, 4).Triangulated();
        double volume = box.Volume();
        var open = RemoveFaces(box, FacesWithNormal(box, Vector3d.UnitZ));
        int vertexCount = open.VertexCount;
        var loop = Assert.Single(open.BoundaryLoops());

        var filled = HoleFiller.FillMinimal(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        // No new vertices, exactly n-2 triangles, and the cap is restored exactly: the
        // minimum-weight triangulation of a planar rim IS the plane.
        Assert.Equal(vertexCount, filled.VertexCount);
        Assert.Equal(open.FaceCount + loop.Count - 2, filled.FaceCount);
        Assert.Equal(volume, filled.Volume(), 12);
    }

    [Fact]
    public void FillMinimal_ReconstructsACreaseThatCrossesTheHole()
    {
        // Two adjacent faces of a box removed: the rim is an L-shaped hexagon straddling the
        // box's edge. A minimum-DIHEDRAL fill puts the two original faces back rather than
        // cutting a flat chord across the corner, so the box's volume is restored exactly —
        // this is the whole point of the tier.
        var box = MeshPrimitives.Box(2, 2, 2).Triangulated();
        double volume = box.Volume();
        var open = RemoveFaces(box,
            [.. FacesWithNormal(box, Vector3d.UnitZ), .. FacesWithNormal(box, Vector3d.UnitX)]);
        var loop = Assert.Single(open.BoundaryLoops());
        Assert.Equal(6, loop.Count);

        var filled = HoleFiller.FillMinimal(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.Equal(volume, filled.Volume(), 12);
    }

    [Fact]
    public void FillMinimal_NonPlanarSaddleRim_ClosesWithoutNewVertices()
    {
        var open = SphereWithBiteRemoved(16, 10);
        int vertexCount = open.VertexCount;
        var loop = Assert.Single(open.BoundaryLoops());

        var filled = HoleFiller.FillMinimal(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.Equal(vertexCount, filled.VertexCount);
        // Flat cap over a spherical hole: the volume drops by the removed dome, and nothing
        // bulges out (the fill interpolates the rim, so it cannot exceed the sphere).
        Assert.InRange(filled.Volume(), 0.95 * open.SignedVolume(), 4.0 / 3.0 * Math.PI);
    }

    [Fact]
    public void FillMinimal_IsDeterministic()
    {
        var open = SphereWithBiteRemoved(20, 12);
        var loop = open.BoundaryLoops()[0];

        var first = HoleFiller.FillMinimal(open, loop);
        var second = HoleFiller.FillMinimal(open, loop);

        Assert.Equal(first.FaceCount, second.FaceCount);
        // The dynamic program has no ordering effects at all, unlike the iterative flip
        // optimizer it replaces: the same input gives the same triangulation, bit for bit.
        Assert.Equal(BitConverter.DoubleToInt64Bits(first.SignedVolume()),
                     BitConverter.DoubleToInt64Bits(second.SignedVolume()));
    }

    // ---- FillSmoothed ----

    [Fact]
    public void FillSmoothed_ClosesTheHoleWithAWellSizedPatch()
    {
        var open = SphereWithBiteRemoved(24, 16);
        var loop = Assert.Single(open.BoundaryLoops());
        double rimEdge = loop.Average(he => he.Vector.Length);

        var filled = HoleFiller.FillSmoothed(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.True(filled.VertexCount > open.VertexCount, "the relaxed patch adds interior vertices");
        // Patch edges are sized to the hole itself: the longest edge anywhere in the result
        // stays within the remesher's band around the rim's own edge length.
        double longest = filled.Edges.Max(e => e.Vector.Length);
        Assert.True(longest <= 1.34 * Math.Max(rimEdge, open.Edges.Max(e => e.Vector.Length)),
            $"longest edge {longest:F4} vs rim edge {rimEdge:F4}");
    }

    [Fact]
    public void FillSmoothed_KeepsTheRimVertexForVertex()
    {
        var open = SphereWithBiteRemoved(20, 12);
        var loop = Assert.Single(open.BoundaryLoops());
        var rim = loop.Select(he => he.Origin.Position).ToList();

        var filled = HoleFiller.FillSmoothed(open, loop);

        // Pinned rim vertices never move and rim edges never split, so every rim vertex is
        // still there at its exact position — that is what makes the stitch weld by index.
        foreach (var p in rim)
            Assert.Contains(filled.Vertices, v => (v.Position - p).Length == 0);
        Assert.True(filled.IsClosed);
    }

    [Fact]
    public void FillSmoothed_TargetEdgeLengthControlsTheDensity()
    {
        var open = SphereWithBiteRemoved(24, 16);
        var loop = open.BoundaryLoops()[0];

        var coarse = HoleFiller.FillSmoothed(open, loop, new SmoothedHoleFillOptions { TargetEdgeLength = 0.3 });
        var fine = HoleFiller.FillSmoothed(open, loop, new SmoothedHoleFillOptions { TargetEdgeLength = 0.05 });

        int coarsePatch = coarse.FaceCount - open.FaceCount;
        int finePatch = fine.FaceCount - open.FaceCount;
        Assert.True(finePatch > coarsePatch * 2,
            $"coarse patch {coarsePatch} faces, fine patch {finePatch}");
        Assert.True(coarse.IsClosed);
        Assert.True(fine.IsClosed);
    }

    // ---- FillAll dispatch ----

    [Fact]
    public void FillAll_MinimalFallback_ClosesWhatFallbackNoneSkips()
    {
        var open = SphereWithBiteRemoved(40, 26); // rim of 40 vertices, well above a fan's comfort

        var filled = HoleFiller.FillAll(open, new HoleFillOptions { MaxSimpleFillVertices = 8 });
        var skipped = HoleFiller.FillAll(open, new HoleFillOptions
        {
            MaxSimpleFillVertices = 8,
            Fallback = HoleFillFallback.None,
        });

        Assert.Equal(HoleFillMethod.Skipped, Assert.Single(skipped.Outcomes).Method);
        Assert.Contains("Fallback", Assert.Single(skipped.Outcomes).Message);
        Assert.False(skipped.Mesh.IsClosed);

        // The default tier closes it, and adds not one vertex doing so.
        Assert.Equal(HoleFillMethod.Minimal, Assert.Single(filled.Outcomes).Method);
        Assert.True(filled.Mesh.IsClosed);
        Assert.Equal(open.VertexCount, filled.Mesh.VertexCount);
    }

    [Fact]
    public void FillAll_SmoothedFallback_ClosesWhatTheDefaultSkips()
    {
        var open = SphereWithBiteRemoved(40, 26);

        var result = HoleFiller.FillAll(open, new HoleFillOptions
        {
            MaxSimpleFillVertices = 8,
            Fallback = HoleFillFallback.Smoothed,
            Smoothed = new SmoothedHoleFillOptions { Iterations = 8 },
        });

        Assert.Equal(HoleFillMethod.Smoothed, Assert.Single(result.Outcomes).Method);
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.True(result.Mesh.VertexCount > open.VertexCount);
    }

    [Fact]
    public void FillAll_MinimalFallback_RespectsItsCubicSizeCap()
    {
        var open = SphereWithBiteRemoved(40, 26);

        var result = HoleFiller.FillAll(open, new HoleFillOptions
        {
            MaxSimpleFillVertices = 8,
            Fallback = HoleFillFallback.Minimal,
            MaxMinimalFillVertices = 10,
        });

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(HoleFillMethod.Skipped, outcome.Method);
        Assert.Contains("MaxMinimalFillVertices", outcome.Message);
        Assert.False(result.Mesh.IsClosed);
    }

    [Fact]
    public void FillAll_DefaultFallbackIsMinimal()
    {
        // Minimal is not a "guess something" tier: every vertex of its patch is already a
        // vertex of the hole, so it interpolates the rim exactly and cannot bulge. That is
        // what makes it a safe default for a closure-seeking pipeline, and the honest
        // report survives wherever it genuinely cannot decide (see the size-cap case).
        var open = SphereWithBiteRemoved(40, 26);
        var result = HoleFiller.FillAll(open, new HoleFillOptions { MaxSimpleFillVertices = 8 });

        Assert.Equal(HoleFillMethod.Minimal, Assert.Single(result.Outcomes).Method);
        Assert.Equal(HoleFillFallback.Minimal, HoleFillOptions.Default.Fallback);
    }
}
