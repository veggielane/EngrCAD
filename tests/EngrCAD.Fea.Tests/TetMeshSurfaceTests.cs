using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="TetMesh.SurfaceOf"/> — the cutaway view, and the only way to look at a tet
/// mesh's interior, since its boundary IS the input surface by contract.
/// </summary>
public class TetMeshSurfaceTests
{
    [Fact]
    public void SelectingEverything_ReproducesTheBoundary()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 3, 2))));

        var all = tets.SurfaceOf(_ => true);
        Assert.True(all.IsClosed);
        Assert.Equal(tets.BoundaryFacetCount, all.FaceCount);
        Assert.Equal(tets.Volume, all.Volume(), Math.Abs(tets.Volume) * 1e-12);
    }

    [Fact]
    public void SelectingHalfTheElements_EnclosesRoughlyHalfTheVolume()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(10, 10, 10))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 3.0 });

        double kept = 0;
        var half = tets.SurfaceOf(t =>
        {
            var e = tets.GetTet(t);
            var centroid = (tets.Position(e.A) + tets.Position(e.B)
                          + tets.Position(e.C) + tets.Position(e.D)) * 0.25;
            bool include = centroid.X < 5;
            if (include) kept += tets.TetVolume(t);
            return include;
        });

        // The extracted surface encloses EXACTLY the selected elements' volume - that is the
        // statement that the cut surface is the right one, not merely a plausible one.
        Assert.Equal(kept, half.Volume(), Math.Abs(kept) * 1e-9);
        Assert.True(half.IsClosed);
        Assert.InRange(kept, 400, 600);
    }

    [Fact]
    public void SelectingNothing_GivesAnEmptySurface()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 2, 2))));
        var none = tets.SurfaceOf(_ => false);
        Assert.Equal(0, none.FaceCount);
    }

    [Fact]
    public void SelectingASingleElement_GivesThatTetrahedron()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 2, 2))));

        var one = tets.SurfaceOf(t => t == 0);
        Assert.Equal(4, one.FaceCount);
        Assert.Equal(4, one.VertexCount);
        Assert.True(one.IsClosed);
        Assert.Equal(tets.TetVolume(0), one.Volume(), Math.Abs(tets.TetVolume(0)) * 1e-12);
    }

    [Fact]
    public void ADisconnectedSelection_StillProducesAValidSurface()
    {
        // Two element groups with nothing between them: HalfEdgeMesh.Build accepts
        // disconnected components, so the cutaway does not have to be one piece.
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(12, 4, 4))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 2.0 });

        double kept = 0;
        var ends = tets.SurfaceOf(t =>
        {
            var e = tets.GetTet(t);
            double x = (tets.Position(e.A).X + tets.Position(e.B).X
                      + tets.Position(e.C).X + tets.Position(e.D).X) * 0.25;
            bool include = x < 3 || x > 9;
            if (include) kept += tets.TetVolume(t);
            return include;
        });

        Assert.Equal(kept, ends.Volume(), Math.Abs(kept) * 1e-9);
    }

    [Fact]
    public void Shrinking_GivesDisjointElementsThatAreManifoldWhateverTheSelection()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.UvSphere(3.0, 16, 8));

        // Every element, each as its own body: 4 faces and 4 vertices apiece, no welding.
        var all = tets.SurfaceOf(_ => true, shrink: 0.8);
        Assert.Equal(4 * tets.TetCount, all.FaceCount);
        Assert.Equal(4 * tets.TetCount, all.VertexCount);
        Assert.True(all.IsClosed);

        // Volume scales as the cube of the shrink factor, per element and so in total.
        Assert.Equal(tets.Volume * 0.8 * 0.8 * 0.8, all.Volume(), Math.Abs(tets.Volume) * 1e-9);
    }

    [Fact]
    public void Shrink_IsValidatedRatherThanSilentlyClamped()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => tets.SurfaceOf(_ => true, shrink: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tets.SurfaceOf(_ => true, shrink: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => tets.SurfaceOf(_ => true, shrink: -1));
    }

    [Fact]
    public void EveryExtractedSurfaceIsOutwardWound_SoItsVolumeIsPositive()
    {
        var tets = TetMesher.Mesh(MeshPrimitives.UvSphere(3.0, 16, 8));

        Assert.True(tets.SurfaceOf(_ => true).Volume() > 0);
        Assert.True(tets.SurfaceOf(_ => true, shrink: 0.85).Volume() > 0);
        Assert.True(tets.SurfaceOf(t => t % 3 == 0, shrink: 0.85).Volume() > 0);
    }

    [Fact]
    public void AWeldedSelectionThatIsNonManifold_RefusesAndNamesTheFix()
    {
        // This is the common case, not a hypothetical: an arbitrary half-space of a tet mesh
        // readily leaves two elements meeting at only a vertex, which is a bow-tie — measured
        // on both a half-sphere and a refined box. The welded form must say so and point at
        // `shrink` rather than leaking the mesh builder's message, and the shrunk form must
        // then succeed on the very same selection.
        var tets = TetMesher.Mesh(MeshPrimitives.UvSphere(3.0, 16, 8));
        bool UpperHalf(int t)
        {
            var e = tets.GetTet(t);
            return (tets.Position(e.A).Z + tets.Position(e.B).Z
                  + tets.Position(e.C).Z + tets.Position(e.D).Z) * 0.25 > 0;
        }

        var ex = Assert.Throws<TetMeshException>(() => tets.SurfaceOf(UpperHalf));
        Assert.Contains("bow-tie", ex.Message);
        Assert.Contains("shrink", ex.Message);

        var shrunk = tets.SurfaceOf(UpperHalf, shrink: 0.9);
        Assert.True(shrunk.IsClosed);
        Assert.True(shrunk.Volume() > 0);
    }
}
