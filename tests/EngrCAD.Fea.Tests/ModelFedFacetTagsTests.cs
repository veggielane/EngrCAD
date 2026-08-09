using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The model-fed boundary-condition seam: a solve whose supports and loads are named by
/// B-Rep face (through <see cref="BRepTessellator.TessellateForTetMesh"/>'s auto-populated
/// <c>FacetTags</c> and <see cref="Facets.Tag"/>) must be BIT-IDENTICAL to the same solve
/// named by hand with a geometric selector — because the two must resolve to the same facet
/// set, and one mesh feeds both.
/// </summary>
public class ModelFedFacetTagsTests
{
    private static int PlanarFaceIndex(IReadOnlyList<BrepFace> faces, Vector3d normal, double offset)
    {
        for (int i = 0; i < faces.Count; i++)
            if (faces[i].IsPlanar(out var o, out var n)
                && Math.Abs(n.Dot(normal) - 1) < 1e-9
                && Math.Abs(o.Dot(normal) - offset) < 1e-9)
                return i;
        throw new Xunit.Sdk.XunitException($"no planar face with normal {normal} at offset {offset}");
    }

    private static FacetRef Ref(TetMesh tets, TetFacet facet)
    {
        var a = tets.Position(facet.V0);
        var b = tets.Position(facet.V1);
        var c = tets.Position(facet.V2);
        var cross = (b - a).Cross(c - a);
        double area = 0.5 * cross.Length;
        var normal = cross.Normalized();
        return new FacetRef(facet.Tet, facet.SourceTriangle, (a + b + c) / 3.0, normal, area);
    }

    [Fact]
    public void FaceSelectorViaTags_MatchesGeometricSelector_AndTheSolveIsBitIdentical()
    {
        const double sx = 20, sy = 12, sz = 8;
        var solid = Shape.Box(sx, sy, sz).ToBrep();
        var faces = solid.Faces.ToList();
        int topIdx = PlanarFaceIndex(faces, Vector3d.UnitZ, sz / 2);
        int bottomIdx = PlanarFaceIndex(faces, -Vector3d.UnitZ, sz / 2);   // normal·origin = +sz/2

        var (mesh, tags) = BRepTessellator.TessellateForTetMesh(solid);
        var tets = TetMesher.Mesh(mesh,
            new TetMeshOptions { FacetTags = tags, RefineQuality = true, MaxElementSize = 5 });

        // The two selectors must pick the SAME facets — the direct statement that a B-Rep face
        // tag names the same surface a geometric selector does.
        var tagTop = Facets.Tag(topIdx);
        var planeTop = Facets.OnPlane(new Vector3d(0, 0, sz / 2), Vector3d.UnitZ);
        var tagBottom = Facets.Tag(bottomIdx);
        var planeBottom = Facets.OnPlane(new Vector3d(0, 0, -sz / 2), -Vector3d.UnitZ);
        int topFacets = 0;
        foreach (var facet in tets.BoundaryFacets)
        {
            var r = Ref(tets, facet);
            Assert.Equal(planeTop(r), tagTop(r));
            Assert.Equal(planeBottom(r), tagBottom(r));
            if (tagTop(r)) topFacets++;
        }
        Assert.True(topFacets > 0, "the load face must carry facets");

        var force = new Vector3d(0, 0, -1500);
        var auto = StructuralSolver.Solve(
            new StructuralModel(tets, Materials.Steel).Fix(tagBottom).Force(tagTop, force));
        var hand = StructuralSolver.Solve(
            new StructuralModel(tets, Materials.Steel).Fix(planeBottom).Force(planeTop, force));

        Assert.Equal(auto.Displacement.Count, hand.Displacement.Count);
        for (int i = 0; i < auto.Displacement.Count; i++)
        {
            AssertBitIdentical(auto.Displacement[i].X, hand.Displacement[i].X);
            AssertBitIdentical(auto.Displacement[i].Y, hand.Displacement[i].Y);
            AssertBitIdentical(auto.Displacement[i].Z, hand.Displacement[i].Z);
        }
        Assert.True(hand.MaxDisplacement > 0, "a loaded, supported bar deflects");
    }

    private static void AssertBitIdentical(double a, double b) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
}
