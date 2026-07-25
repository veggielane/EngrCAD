using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class HoleFillerTests
{
    /// <summary>Removes the faces at the given indices, rebuilding the mesh (punches holes).</summary>
    private static HalfEdgeMesh RemoveFaces(HalfEdgeMesh mesh, params int[] faceIndices)
    {
        var (positions, faces) = mesh.ToIndexed();
        var remove = new HashSet<int>(faceIndices);
        var kept = faces.Where((_, i) => !remove.Contains(i)).ToList();
        return HalfEdgeMesh.Build(positions, kept);
    }

    private static int FaceWithNormal(HalfEdgeMesh mesh, Vector3d normal) =>
        mesh.Faces.First(f => f.Normal().Dot(normal) > 0.9).Index;

    // ---- FillSimple ----

    [Fact]
    public void FillSimple_QuadHoleInBox_RestoresClosedMeshAndExactVolume()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        double volume = box.Volume();
        var open = RemoveFaces(box, FaceWithNormal(box, Vector3d.UnitZ));
        Assert.False(open.IsClosed);

        var loop = Assert.Single(open.BoundaryLoops());
        var filled = HoleFiller.FillSimple(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        // The fan centroid lies in the planar cap, so the volume is exactly restored.
        Assert.Equal(volume, filled.Volume(), 12);
    }

    [Fact]
    public void FillSimple_TriangleHole_AddsSingleTriangleNoCentroid()
    {
        var sphere = MeshPrimitives.UvSphere(1, 8, 4);
        // Remove one interior (non-pole) triangle-fan face? UV sphere mid faces are quads;
        // triangulate first so every face is a triangle, then punch one.
        var tri = sphere.Triangulated();
        int vertexCount = tri.VertexCount;
        var open = RemoveFaces(tri, tri.FaceCount / 2);

        var loop = Assert.Single(open.BoundaryLoops());
        Assert.Equal(3, loop.Count);
        var filled = HoleFiller.FillSimple(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.Equal(vertexCount, filled.VertexCount); // no centroid vertex added
        Assert.Equal(tri.Volume(), filled.Volume(), 12);
    }

    [Fact]
    public void FillSimple_WildlyNonPlanarLargeLoop_Throws()
    {
        var mesh = CrownedTube(16, radius: 5, height: 2, crownAmplitude: 4);
        var crown = mesh.BoundaryLoops().Single(l => l.Any(he => he.Origin.Position.Z > 1));

        var ex = Assert.Throws<InvalidOperationException>(() => HoleFiller.FillSimple(mesh, crown));
        Assert.Contains("best-fit plane", ex.Message);
    }

    [Fact]
    public void FillSimple_RejectsNonBoundaryLoop()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var interior = box.Faces.First().HalfEdges().ToList();
        Assert.Throws<ArgumentException>(() => HoleFiller.FillSimple(box, interior));
    }

    // ---- FillPlanar ----

    [Fact]
    public void FillPlanar_QuadHoleInBox_RestoresClosedMeshAndExactVolume()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        double volume = box.Volume();
        var open = RemoveFaces(box, FaceWithNormal(box, -Vector3d.UnitX));

        var loop = Assert.Single(open.BoundaryLoops());
        var filled = HoleFiller.FillPlanar(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.Equal(volume, filled.Volume(), 12);
    }

    [Fact]
    public void FillPlanar_NonPlanarLoop_Throws()
    {
        var sphere = MeshPrimitives.UvSphere(1, 12, 6);
        // Remove the faces around one mid-latitude vertex: the one-ring rim is non-planar
        // by the sphere's sagitta, far beyond the weld-tolerance planarity default.
        var vertex = sphere.Vertices.First(v => Math.Abs(v.Position.Z) < 0.3 && v.Valence >= 4);
        var open = RemoveFaces(sphere, [.. vertex.IncidentFaces().Select(f => f.Index)]);

        var loop = Assert.Single(open.BoundaryLoops());
        Assert.Throws<ArgumentException>(() => HoleFiller.FillPlanar(open, loop));
    }

    [Fact]
    public void FillPlanar_AnnularHole_FillsOuterPlusInnerAsPolygonWithHole()
    {
        var tube = Tube(segments: 24, outerRadius: 5, innerRadius: 2, height: 3);
        Assert.Equal(2, tube.BoundaryLoops().Count); // open top: outer + inner rim

        var filled = HoleFiller.FillPlanar(tube, [.. tube.BoundaryLoops()]);

        filled.Validate();
        Assert.True(filled.IsClosed);
        // Prismatic polygon tube: volume is exactly ring area × height.
        double expected = (PolygonRingArea(24, 5) - PolygonRingArea(24, 2)) * 3;
        Assert.Equal(expected, filled.Volume(), 9);
    }

    [Fact]
    public void FillPlanar_RimWithExactlyCollinearVertex_ChordZipKeepsItWelded()
    {
        // Open 2x2x2 box whose +X wall is split so the top rim carries a vertex exactly
        // collinear between two corners (the T-junction shape tessellation produces).
        // Earcut filters exactly-collinear vertices; the ring-aware chord zip must
        // re-expand the chord — otherwise the walls still referencing the midpoint are
        // left with an unweldable crack and the result cannot close.
        var positions = new List<Vector3d>
        {
            (0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), // b0..b3
            (0, 0, 2), (2, 0, 2), (2, 2, 2), (0, 2, 2), // t0..t3
            (2, 1, 2),                                  // m: midpoint of the t1-t2 rim edge
        };
        var faces = new List<int[]>
        {
            new[] { 0, 3, 2, 1 }, // bottom, outward -Z
            new[] { 0, 1, 5, 4 }, // y = 0 wall
            new[] { 1, 2, 6, 8 }, // +X wall quad (b1, b2, t2, m)
            new[] { 1, 8, 5 },    // +X wall triangle (b1, m, t1)
            new[] { 2, 3, 7, 6 }, // y = 2 wall
            new[] { 3, 0, 4, 7 }, // x = 0 wall
        };
        var open = HalfEdgeMesh.Build(positions, faces);
        var loop = Assert.Single(open.BoundaryLoops());
        Assert.Equal(5, loop.Count); // rim includes the collinear midpoint

        var filled = HoleFiller.FillPlanar(open, loop);

        filled.Validate();
        Assert.True(filled.IsClosed);
        Assert.Equal(9, filled.VertexCount); // the midpoint stayed referenced
        Assert.Equal(8.0, filled.Volume(), 12);
    }

    // ---- FillAll ----

    [Fact]
    public void FillAll_ClosedMesh_ReturnsSameMeshNoOutcomes()
    {
        var box = MeshPrimitives.Box(1, 2, 3);
        var result = HoleFiller.FillAll(box);
        Assert.Same(box, result.Mesh);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public void FillAll_TwoPlanarHolesOnParallelPlanes_BothPlanarFilledSeparately()
    {
        // Opposite faces: same plane normal, different offsets — the coplanar grouping must
        // separate them (it measures full plane deviation, not just normal alignment).
        var box = MeshPrimitives.Box(2, 2, 2);
        double volume = box.Volume();
        var open = RemoveFaces(box, FaceWithNormal(box, Vector3d.UnitZ), FaceWithNormal(box, -Vector3d.UnitZ));

        var result = HoleFiller.FillAll(open);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, o => Assert.Equal(HoleFillMethod.Planar, o.Method));
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        Assert.Equal(volume, result.Mesh.Volume(), 12);
    }

    [Fact]
    public void FillAll_AnnularHole_GroupsCoplanarLoopsIntoOnePlanarFill()
    {
        var tube = Tube(segments: 32, outerRadius: 4, innerRadius: 1.5, height: 2);
        var result = HoleFiller.FillAll(tube);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, o => Assert.Equal(HoleFillMethod.Planar, o.Method));
        Assert.All(result.Outcomes, o => Assert.Contains("coplanar", o.Message));
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        double expected = (PolygonRingArea(32, 4) - PolygonRingArea(32, 1.5)) * 2;
        Assert.Equal(expected, result.Mesh.Volume(), 9);
    }

    [Fact]
    public void FillAll_SmallNonPlanarHoleOnSphere_SimpleFillCloseToOriginalVolume()
    {
        var sphere = MeshPrimitives.UvSphere(1, 16, 8);
        double volume = sphere.Volume();
        var vertex = sphere.Vertices.First(v => Math.Abs(v.Position.Z) < 0.3 && v.Valence >= 4);
        var open = RemoveFaces(sphere, [.. vertex.IncidentFaces().Select(f => f.Index)]);

        var result = HoleFiller.FillAll(open);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(HoleFillMethod.Simple, outcome.Method);
        result.Mesh.Validate();
        Assert.True(result.Mesh.IsClosed);
        // The fan flattens the removed one-ring dome slightly (deficit ~ sagitta x hole
        // area, a tiny fraction of total volume), so the filled volume sits just below the
        // original — the discretization-derived bound.
        Assert.InRange(result.Mesh.Volume(), volume * 0.99, volume);
    }

    [Fact]
    public void FillAll_SaddleCrownLoop_SkippedWithReason_PlanarRimStillFilled()
    {
        var mesh = CrownedTube(16, radius: 5, height: 2, crownAmplitude: 4);
        Assert.Equal(2, mesh.BoundaryLoops().Count);

        var result = HoleFiller.FillAll(mesh);

        Assert.Equal(2, result.Outcomes.Count);
        var skipped = Assert.Single(result.Outcomes, o => o.Method == HoleFillMethod.Skipped);
        Assert.Contains("best-fit plane", skipped.Message);
        Assert.Single(result.Outcomes, o => o.Method == HoleFillMethod.Planar);
        result.Mesh.Validate();
        Assert.False(result.Mesh.IsClosed); // the saddle rim stays open
        Assert.Single(result.Mesh.BoundaryLoops());
    }

    [Fact]
    public void FillAll_LoopAboveSizeThreshold_Skipped()
    {
        // The crown rim is a hair off-plane (1e-3 >> the 1e-9 planarity default) so it falls
        // to the simple path, where the vertex-count cap rejects it; the flat bottom rim
        // still planar-fills.
        var mesh = CrownedTube(16, radius: 5, height: 2, crownAmplitude: 1e-3);
        var result = HoleFiller.FillAll(mesh, new HoleFillOptions { MaxSimpleFillVertices = 8 });

        Assert.Equal(2, result.Outcomes.Count);
        Assert.Single(result.Outcomes, o => o.Method == HoleFillMethod.Planar);
        var skipped = Assert.Single(result.Outcomes, o => o.Method == HoleFillMethod.Skipped);
        Assert.Contains("MaxSimpleFillVertices", skipped.Message);
    }

    // ---- helpers ----

    /// <summary>Exact area of the regular n-gon inscribed in a circle of the given radius.</summary>
    private static double PolygonRingArea(int n, double radius) =>
        0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n);

    /// <summary>
    /// Open prismatic tube: outer wall, inner wall, and a flat bottom annulus; the top is
    /// open, leaving two coplanar boundary loops (outer rim + inner rim) at z = height.
    /// </summary>
    private static HalfEdgeMesh Tube(int segments, double outerRadius, double innerRadius, double height)
    {
        var positions = new List<Vector3d>();
        for (int ring = 0; ring < 2; ring++) // 0 = outer, 1 = inner
        {
            double r = ring == 0 ? outerRadius : innerRadius;
            for (int level = 0; level < 2; level++) // 0 = bottom, 1 = top
            {
                for (int i = 0; i < segments; i++)
                {
                    double a = 2 * Math.PI * i / segments;
                    positions.Add(new Vector3d(r * Math.Cos(a), r * Math.Sin(a), level * height));
                }
            }
        }
        int OuterBottom(int i) => i % segments;
        int OuterTop(int i) => segments + i % segments;
        int InnerBottom(int i) => 2 * segments + i % segments;
        int InnerTop(int i) => 3 * segments + i % segments;

        var faces = new List<int[]>();
        for (int i = 0; i < segments; i++)
        {
            // Outer wall, outward (radially out): CCW seen from outside.
            faces.Add([OuterBottom(i), OuterBottom(i + 1), OuterTop(i + 1), OuterTop(i)]);
            // Inner wall, outward = radially inward.
            faces.Add([InnerBottom(i + 1), InnerBottom(i), InnerTop(i), InnerTop(i + 1)]);
            // Bottom annulus, outward = -Z.
            faces.Add([OuterBottom(i + 1), OuterBottom(i), InnerBottom(i), InnerBottom(i + 1)]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>
    /// Open tube whose bottom rim is planar (z = 0) and whose top rim alternates z between
    /// height ± crownAmplitude (a crown/saddle rim — wildly non-planar for large amplitude).
    /// </summary>
    private static HalfEdgeMesh CrownedTube(int segments, double radius, double height, double crownAmplitude)
    {
        var positions = new List<Vector3d>();
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            positions.Add(new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), 0));
        }
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            double z = height + (i % 2 == 0 ? crownAmplitude : -crownAmplitude);
            positions.Add(new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), z));
        }
        var faces = new List<int[]>();
        for (int i = 0; i < segments; i++)
        {
            int b0 = i, b1 = (i + 1) % segments;
            faces.Add([b0, b1, segments + b1, segments + b0]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }
}
