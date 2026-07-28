using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

public class TetMesherTests
{
    // ---- the volume identity: the closed-form oracle ----

    [Fact]
    public void UnitCube_FillsExactlyItsOwnVolume()
    {
        var cube = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1)));
        var tets = TetMesher.Mesh(cube, null, out var report);

        Assert.True(tets.TetCount >= 5, $"a cube needs at least 5 tetrahedra, got {tets.TetCount}");
        Assert.Equal(1.0, tets.Volume, 12);
        Assert.True(report.VolumeResidual < 1e-12, $"volume residual {report.VolumeResidual:E3}");
        Assert.Equal(0, report.RecoveryRounds); // a box's triangles are already Delaunay faces
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ScaleFreedom_TheVolumeIdentityHoldsOverSixDecades(double scale)
    {
        var box = MeshPrimitives.Box(new Aabb(
            new Vector3d(0, 0, 0), new Vector3d(2 * scale, 3 * scale, 5 * scale)));
        var tets = TetMesher.Mesh(box, null, out var report);

        double expected = 30 * scale * scale * scale;
        Assert.Equal(expected, tets.Volume, Math.Abs(expected) * 1e-9);
        Assert.True(report.VolumeResidual < 1e-9, $"residual {report.VolumeResidual:E3} at scale {scale}");
    }

    [Fact]
    public void Sphere_FillsItsPolyhedronExactlyAndConvergesQuadraticallyOnTheAnalyticVolume()
    {
        // Two separate claims, and keeping them separate is the point. The MESHER's claim is
        // that the tets fill the input polyhedron and nothing else, which is the residual
        // against the surface's own volume. The remaining error against the analytic sphere
        // belongs entirely to the inscribed polyhedron, so the right assertion is that the
        // tet mesh's error EQUALS the surface's - not that it clears some chosen threshold.
        double analytic = 4.0 / 3.0 * Math.PI * 8.0;
        var errors = new List<double>();

        foreach (int segments in new[] { 12, 24, 48 })
        {
            var sphere = MeshPrimitives.UvSphere(2.0, segments, segments / 2);
            var tets = TetMesher.Mesh(sphere, null, out var report);

            Assert.True(report.VolumeResidual < 1e-13,
                $"{segments} segments: residual {report.VolumeResidual:E3}");
            Assert.Equal(0, report.RecoveryRounds); // a UV sphere is already boundary-conforming

            double surfaceError = Math.Abs(sphere.Volume() - analytic) / analytic;
            double tetError = Math.Abs(tets.Volume - analytic) / analytic;
            Assert.Equal(surfaceError, tetError, surfaceError * 1e-9);
            errors.Add(tetError);
        }

        // Inscribed-polyhedron error is O(h^2), so halving h must quarter it. Measured
        // 3.86 / 3.97 (and 3.99 at 96 segments) - the ratio approaches 4 from below.
        for (int i = 1; i < errors.Count; i++)
        {
            double ratio = errors[i - 1] / errors[i];
            Assert.InRange(ratio, 3.5, 4.2);
        }
    }

    [Fact]
    public void Cylinder_FillsItsFacetedVolumeAndKeepsEveryBoundaryFacet()
    {
        var cylinder = MeshPrimitives.Cylinder(1.5, 4.0, 32);
        var tets = TetMesher.Mesh(cylinder, null, out var report);

        Assert.True(report.VolumeResidual < 1e-11, $"residual {report.VolumeResidual:E3}");
        AssertBoundaryIsClosedAndOutward(tets);

        // Every boundary facet maps to a real input triangle.
        int triangles = cylinder.Triangulated().FaceCount;
        Assert.All(tets.BoundaryFacets, f => Assert.InRange(f.SourceTriangle, 0, triangles - 1));
        Assert.True(report.BoundaryFacets >= triangles);
    }

    // ---- structural guarantees ----

    [Fact]
    public void EveryTetIsPositivelyOrientedAndTheBoundaryIsAClosedManifold()
    {
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(-1, -2, -3), new Vector3d(4, 5, 6)));
        var tets = TetMesher.Mesh(box);

        for (int t = 0; t < tets.TetCount; t++)
            Assert.True(tets.TetVolume(t) > 0);

        AssertBoundaryIsClosedAndOutward(tets);

        // The boundary re-builds as a manifold HalfEdgeMesh - the strongest structural
        // statement available, since Build rejects bow-ties and inconsistent winding.
        var surface = tets.BoundaryMesh(out _);
        Assert.True(surface.IsClosed);
        Assert.Equal(tets.Volume, surface.Volume(), Math.Abs(tets.Volume) * 1e-9);
    }

    [Fact]
    public void InteriorFacesArePairedExactlyOnce_SoTheMeshHasNoCracks()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 8);
        var tets = TetMesher.Mesh(sphere);

        var counts = new Dictionary<(int, int, int), int>();
        for (int t = 0; t < tets.TetCount; t++)
        {
            var tet = tets.GetTet(t);
            foreach (var (a, b, c) in Faces(tet))
            {
                var key = Sorted(a, b, c);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        int boundary = counts.Count(kv => kv.Value == 1);
        int interior = counts.Count(kv => kv.Value == 2);
        int broken = counts.Count(kv => kv.Value > 2);

        Assert.Equal(0, broken);
        Assert.Equal(tets.BoundaryFacetCount, boundary);
        Assert.True(interior > 0);
    }

    [Fact]
    public void Determinism_TwoRunsProduceBitIdenticalMeshes()
    {
        var shape = MeshPrimitives.UvSphere(1.0, 20, 10);
        var a = TetMesher.Mesh(shape);
        var b = TetMesher.Mesh(shape);

        Assert.Equal(a.TetCount, b.TetCount);
        Assert.Equal(a.VertexCount, b.VertexCount);
        for (int i = 0; i < a.VertexCount; i++)
            Assert.Equal(a.Position(i), b.Position(i));
        for (int t = 0; t < a.TetCount; t++)
            Assert.Equal(a.GetTet(t), b.GetTet(t));
        Assert.Equal(a.Volume, b.Volume);
    }

    [Fact]
    public void ClassificationAgreesWithTheIndependentWindingNumber()
    {
        // The mesher classifies combinatorially (flood fill blocked by recovered facets).
        // The winding number is a completely different mechanism, so agreement is evidence.
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 8);
        var tets = TetMesher.Mesh(sphere);
        var winding = new MeshWindingNumber(sphere);

        for (int t = 0; t < tets.TetCount; t++)
        {
            var tet = tets.GetTet(t);
            var centroid = (tets.Position(tet.A) + tets.Position(tet.B)
                          + tets.Position(tet.C) + tets.Position(tet.D)) / 4.0;
            Assert.True(winding.WindingNumber(centroid) > 0.5,
                $"tet {t}'s centroid {centroid} was classified inside but has winding number " +
                $"{winding.WindingNumber(centroid):F3}");
        }
    }

    // ---- topology beyond a convex blob ----

    [Fact]
    public void Torus_MeshesWithGenusOneBoundary()
    {
        var torus = Torus(3.0, 1.0, 24, 12);
        var tets = TetMesher.Mesh(torus, null, out var report);

        Assert.True(report.VolumeResidual < 1e-10, $"residual {report.VolumeResidual:E3}");
        AssertBoundaryIsClosedAndOutward(tets);

        var surface = tets.BoundaryMesh(out _);
        // Euler characteristic V - E + F = 2 - 2g; a torus is genus 1, so chi = 0.
        int chi = surface.VertexCount - surface.EdgeCount + surface.FaceCount;
        Assert.Equal(0, chi);
    }

    [Fact]
    public void HollowShell_KeepsTheCavityEmpty()
    {
        // Two nested spheres: the outer surface bounds material, the inner one bounds a
        // void. Classification must fill the shell and leave the cavity unmeshed - which the
        // volume identity states precisely.
        var outer = MeshPrimitives.UvSphere(2.0, 20, 10);
        var inner = Reversed(MeshPrimitives.UvSphere(1.0, 20, 10));
        var shell = Merge(outer, inner);

        var tets = TetMesher.Mesh(shell, null, out var report);

        Assert.True(report.VolumeResidual < 1e-10, $"residual {report.VolumeResidual:E3}");
        double expected = 4.0 / 3.0 * Math.PI * (8.0 - 1.0);
        Assert.Equal(expected, tets.Volume, expected * 0.05);

        // No tetrahedron may sit in the cavity: every centroid must be at radius > 1 - slack.
        for (int t = 0; t < tets.TetCount; t++)
        {
            var tet = tets.GetTet(t);
            var centroid = (tets.Position(tet.A) + tets.Position(tet.B)
                          + tets.Position(tet.C) + tets.Position(tet.D)) / 4.0;
            Assert.True(centroid.Length > 0.9, $"tet {t} sits in the cavity at radius {centroid.Length:F3}");
        }
    }

    [Fact]
    public void TwoDisjointBodies_GetDistinctRegionIds()
    {
        var a = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1)));
        var b = MeshPrimitives.Box(new Aabb(new Vector3d(3, 0, 0), new Vector3d(4, 1, 1)));

        var tets = TetMesher.Mesh([a, b], null, out var report);

        Assert.Equal(2.0, tets.Volume, 12);
        Assert.Equal(new[] { 0, 1 }, tets.Regions);
        Assert.True(report.VolumeResidual < 1e-12);

        // Each region's own volume is 1.
        for (int region = 0; region < 2; region++)
        {
            double volume = 0;
            for (int t = 0; t < tets.TetCount; t++)
                if (tets.RegionOf(t) == region)
                    volume += tets.TetVolume(t);
            Assert.Equal(1.0, volume, 12);
        }
    }

    // ---- refusals ----

    [Fact]
    public void OpenSurface_IsRefusedByName()
    {
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1)));
        var (positions, faces) = box.Triangulated().ToIndexed();
        faces.RemoveAt(0);
        var open = HalfEdgeMesh.Build(positions, faces);

        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(open));
        Assert.Contains("CLOSED", ex.Message);
        Assert.Contains("AutoRepair", ex.Message);
    }

    [Fact]
    public void InwardWoundSurface_IsRefusedByName()
    {
        var inward = Reversed(MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1))));
        var ex = Assert.Throws<TetMeshException>(() => TetMesher.Mesh(inward));
        Assert.Contains("non-positive volume", ex.Message);
    }

    [Fact]
    public void SteinerBudget_IsRefusedByNameRatherThanTruncated()
    {
        // A budget of zero makes any recovery impossible; the mesher must say so rather than
        // return a mesh whose boundary silently is not the input surface.
        var sphere = MeshPrimitives.UvSphere(1.0, 6, 4);
        var options = new TetMeshOptions { MaxSteinerPoints = 0, MaxRecoveryRounds = 12 };

        // A coarse sphere may or may not need recovery; if it does, the budget must bite.
        try
        {
            var mesh = TetMesher.Mesh(sphere, options, out var report);
            Assert.Equal(0, report.BoundarySteinerPoints);
        }
        catch (TetMeshException ex)
        {
            Assert.Contains("budget", ex.Message);
            Assert.Contains("MaxSteinerPoints", ex.Message);
        }
    }

    // ---- helpers ----

    private static void AssertBoundaryIsClosedAndOutward(TetMesh tets)
    {
        var centroid = Vector3d.Zero;
        for (int i = 0; i < tets.VertexCount; i++)
            centroid += tets.Position(i);
        centroid /= tets.VertexCount;

        // Divergence theorem: summing (1/3) * centroid-relative position . area-normal over
        // outward facets gives the enclosed volume. Any inward facet would subtract.
        double volume = 0;
        foreach (var f in tets.BoundaryFacets)
        {
            var a = tets.Position(f.V0) - centroid;
            var b = tets.Position(f.V1) - centroid;
            var c = tets.Position(f.V2) - centroid;
            volume += a.Dot(b.Cross(c)) / 6.0;
        }
        Assert.Equal(tets.Volume, volume, Math.Abs(tets.Volume) * 1e-9);
    }

    private static IEnumerable<(int, int, int)> Faces(Tet t)
    {
        yield return (t.A, t.B, t.C);
        yield return (t.A, t.B, t.D);
        yield return (t.A, t.C, t.D);
        yield return (t.B, t.C, t.D);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static HalfEdgeMesh Reversed(HalfEdgeMesh mesh)
    {
        var (positions, faces) = mesh.ToIndexed();
        return HalfEdgeMesh.Build(positions, faces.Select(f => (IReadOnlyList<int>)[.. f.Reverse()]));
    }

    private static HalfEdgeMesh Merge(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var (pa, fa) = a.ToIndexed();
        var (pb, fb) = b.ToIndexed();
        var positions = new List<Vector3d>(pa);
        var faces = new List<IReadOnlyList<int>>(fa);
        int offset = positions.Count;
        positions.AddRange(pb);
        foreach (var f in fb)
            faces.Add([.. f.Select(v => v + offset)]);
        return HalfEdgeMesh.Build(positions, faces);
    }

    internal static HalfEdgeMesh Torus(double major, double minor, int majorSegments, int minorSegments)
    {
        var positions = new List<Vector3d>();
        for (int i = 0; i < majorSegments; i++)
        {
            double u = 2 * Math.PI * i / majorSegments;
            for (int j = 0; j < minorSegments; j++)
            {
                double v = 2 * Math.PI * j / minorSegments;
                double r = major + minor * Math.Cos(v);
                positions.Add(new Vector3d(r * Math.Cos(u), r * Math.Sin(u), minor * Math.Sin(v)));
            }
        }

        var faces = new List<IReadOnlyList<int>>();
        for (int i = 0; i < majorSegments; i++)
        {
            int iNext = (i + 1) % majorSegments;
            for (int j = 0; j < minorSegments; j++)
            {
                int jNext = (j + 1) % minorSegments;
                int a = i * minorSegments + j;
                int b = iNext * minorSegments + j;
                int c = iNext * minorSegments + jNext;
                int d = i * minorSegments + jNext;
                faces.Add([a, b, c]);
                faces.Add([a, c, d]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }
}
