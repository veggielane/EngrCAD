using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshExtrudeTests
{
    private static int FaceWithNormal(HalfEdgeMesh mesh, Vector3d normal) =>
        mesh.Faces.First(f => f.Normal().Dot(normal) > 0.9).Index;

    /// <summary>Flat quad grid in the z = 0 plane over [0, width] × [0, depth], faces CCW from +Z.</summary>
    private static HalfEdgeMesh PlanePatch(double width, double depth, int nx, int ny)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
                positions.Add(new Vector3d(width * i / nx, depth * j / ny, 0));
        }
        int V(int i, int j) => j * (nx + 1) + i;
        var faces = new List<int[]>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
                faces.Add([V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j + 1)]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>Open lower hemisphere of the given radius (UV sphere cut at the equator).</summary>
    private static HalfEdgeMesh Hemisphere(double radius, int segments, int rings)
    {
        var sphere = MeshPrimitives.UvSphere(radius, segments, rings);
        return MeshPlaneCut.Cut(sphere, Vector3d.Zero, Vector3d.UnitZ, cap: false).Mesh;
    }

    // ---- Faces (offset vector) ----

    [Fact]
    public void Faces_BoxTopByOffset_VolumeAddsExactly()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        int top = FaceWithNormal(box, Vector3d.UnitZ);

        var extruded = MeshExtrude.Faces(box, [top], new Vector3d(0, 0, 1));

        extruded.Validate();
        Assert.True(extruded.IsClosed);
        Assert.Equal(10, extruded.FaceCount); // 6 originals (top retargeted) + 4 walls
        Assert.Equal(box.Volume() + 2 * 3 * 1, extruded.Volume(), 12);
        Assert.Equal(2, extruded.EulerCharacteristic);
    }

    [Fact]
    public void Faces_ObliqueOffset_ShearsVolumeUnchangedPrism()
    {
        // Shearing sideways while lifting: prism volume = base area × height component.
        var box = MeshPrimitives.Box(2, 2, 2);
        int top = FaceWithNormal(box, Vector3d.UnitZ);

        var extruded = MeshExtrude.Faces(box, [top], new Vector3d(1, 0.5, 1));

        extruded.Validate();
        Assert.True(extruded.IsClosed);
        Assert.Equal(box.Volume() + 2 * 2 * 1, extruded.Volume(), 12);
    }

    [Fact]
    public void Faces_PreservesInputFaceIndices_WallsAppended()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        int top = FaceWithNormal(box, Vector3d.UnitZ);

        var extruded = MeshExtrude.Faces(box, [top], new Vector3d(0, 0, 2));

        // Untouched faces keep index and geometry; the patch face keeps its index but moved.
        for (int f = 0; f < box.FaceCount; f++)
        {
            if (f == top)
                continue;
            Assert.Equal(box.GetFace(f).Centroid(), extruded.GetFace(f).Centroid());
        }
        Assert.Equal(box.GetFace(top).Centroid() + new Vector3d(0, 0, 2), extruded.GetFace(top).Centroid());
    }

    // ---- Faces (distance along patch normals) ----

    [Fact]
    public void Faces_BoxTopByDistance_MatchesOffsetAlongNormal()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        int top = FaceWithNormal(box, Vector3d.UnitZ);

        var extruded = MeshExtrude.Faces(box, [top], 1.5);

        extruded.Validate();
        Assert.True(extruded.IsClosed);
        Assert.Equal(box.Volume() + 2 * 3 * 1.5, extruded.Volume(), 12);
    }

    [Fact]
    public void Faces_TwoDisjointRegions_EachStitchedIndependently()
    {
        var box = MeshPrimitives.Box(2, 3, 1); // top/bottom faces are 2×3
        int top = FaceWithNormal(box, Vector3d.UnitZ);
        int bottom = FaceWithNormal(box, -Vector3d.UnitZ);

        var extruded = MeshExtrude.Faces(box, [top, bottom], 1.0);

        extruded.Validate();
        Assert.True(extruded.IsClosed);
        Assert.Equal(6 + 8, extruded.FaceCount); // two wall rings of 4
        Assert.Equal(box.Volume() + 2 * (2 * 3 * 1.0), extruded.Volume(), 12);
        Assert.Equal(2, extruded.EulerCharacteristic);
    }

    [Fact]
    public void Faces_PatchTouchingOpenBoundary_StaysManifoldOpen()
    {
        // Extrude a corner quad of an open plane patch: two of its edges lie on the mesh
        // boundary; walls must still stitch and the result stays manifold (and open).
        var plane = PlanePatch(2, 2, 2, 2);
        var extruded = MeshExtrude.Faces(plane, [0], new Vector3d(0, 0, 1));

        extruded.Validate();
        Assert.False(extruded.IsClosed);
        Assert.Equal(4 + 4, extruded.FaceCount);
        // The flat patch lies in z = 0 (zero flux through the plane of the open rim), so the
        // signed volume is exactly the lifted 1×1×1 bump.
        Assert.Equal(1.0, extruded.SignedVolume(), 12);
    }

    [Fact]
    public void Faces_InvalidInputs_Throw()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        Assert.Throws<ArgumentException>(() => MeshExtrude.Faces(box, Array.Empty<int>(), 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshExtrude.Faces(box, [17], 1.0));
    }

    // ---- Thicken ----

    [Fact]
    public void Thicken_PlanePatch_ExactSlabVolume()
    {
        var plane = PlanePatch(2, 2, 4, 4);
        var slab = MeshExtrude.Thicken(plane, 0.5);

        slab.Validate();
        Assert.True(slab.IsClosed);
        // All vertex normals are exactly +Z, so the back skin sits at z = −0.5: a 2×2×0.5 slab.
        Assert.Equal(2.0 * 2.0 * 0.5, slab.Volume(), 12);
        Assert.Equal(2, slab.EulerCharacteristic);
    }

    [Fact]
    public void Thicken_HemisphereShell_ClosedWithAreaTimesThicknessVolume()
    {
        var shell = Hemisphere(radius: 10, segments: 48, rings: 24);
        double area = shell.SurfaceArea();
        double t = 0.05; // thin shell: volume → area × t as t → 0

        var solid = MeshExtrude.Thicken(shell, t);

        solid.Validate();
        Assert.True(solid.IsClosed);
        double volume = solid.Volume();
        // Thin-shell estimate: V ≈ area × t, off by O(t/R) curvature terms plus rim-normal
        // tilt at the equator — a few percent at t/R = 0.005 and this tessellation.
        Assert.InRange(volume, 0.95 * area * t, 1.05 * area * t);
    }

    [Fact]
    public void Thicken_ClosedSphere_HollowShellWallVolume()
    {
        var sphere = MeshPrimitives.UvSphere(2, 32, 16);
        double t = 0.25;

        var hollow = MeshExtrude.Thicken(sphere, t);

        hollow.Validate();
        Assert.True(hollow.IsClosed);
        // Two nested shells: outer = original, inner = reversed offset copy. The offset
        // copy of the discretized sphere is (nearly) the same polyhedron scaled by
        // (R−t)/R, so the wall volume tracks V·(1 − ((R−t)/R)³) closely.
        double expected = sphere.Volume() * (1 - Math.Pow((2 - t) / 2, 3));
        Assert.InRange(hollow.Volume(), 0.98 * expected, 1.02 * expected);
    }

    [Fact]
    public void Thicken_InvalidThickness_Throws()
    {
        var plane = PlanePatch(1, 1, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshExtrude.Thicken(plane, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshExtrude.Thicken(plane, -1));
    }
}
