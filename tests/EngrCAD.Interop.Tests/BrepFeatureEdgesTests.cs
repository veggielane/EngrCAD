using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Feature edges from ACTUAL B-Rep edges (<see cref="BrepFeatureEdges"/>): sampling
/// counts follow the tessellator's edge rules at the requested display resolution,
/// sharpness comes from adjacent-face outward normals, and smooth edges (sphere
/// generator seams, equal-carrier junctions) are omitted. Endpoint assertions are
/// exact-construction (curve evaluation, no meshing), so tolerances are 1e-12 scale.
/// </summary>
public class BrepFeatureEdgesTests
{
    [Fact]
    public void Box_YieldsExactlyItsTwelveEdges()
    {
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4)));
        var segments = BrepFeatureEdges.Extract(solid);

        // 12 line edges, 2 sample points each: one segment per edge, endpoints on
        // box corners (coordinates are exact corner values).
        Assert.Equal(12, segments.Count);
        foreach (var (a, b) in segments)
        {
            Assert.True(IsCorner(a) && IsCorner(b), $"segment endpoint off-corner: {a} -> {b}");
            Assert.NotEqual(a, b);
        }

        static bool IsCorner(in Vector3d p) =>
            (p.X == 0 || p.X == 2) && (p.Y == 0 || p.Y == 3) && (p.Z == 0 || p.Z == 4);
    }

    [Fact]
    public void Cylinder_YieldsTwoRimCirclesAtTheRequestedResolution_NoSeamLine()
    {
        var solid = SolidFactory.MakeCylinder(radius: 2, height: 5);
        var segments = BrepFeatureEdges.Extract(solid, segmentsPerCircle: 64);

        // Two closed rim circles, 64 segments each — the display resolution is the
        // caller's, independent of any mesh. No band seam line ever appears: a seam
        // is either a two-use edge of one periodic face (skipped by construction) or
        // absent from the topology entirely.
        Assert.Equal(2 * 64, segments.Count);
        foreach (var (a, b) in segments)
        {
            // Every segment is a horizontal rim chord: endpoints share a cap plane
            // (z exactly 0 or 5) and sit exactly on the radius-2 circle.
            Assert.Equal(a.Z, b.Z);
            Assert.True(a.Z == 0 || Math.Abs(a.Z - 5) < 1e-12);
            Assert.Equal(2, Radius(a), 1e-12);
            Assert.Equal(2, Radius(b), 1e-12);
        }

        static double Radius(in Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
    }

    [Fact]
    public void Sphere_HasNoFeatureEdges()
    {
        // A sphere's only edges are the pole-to-pole generator seams between halves
        // of the same smooth carrier: normals agree, nothing is drawn — where mesh
        // dihedral extraction would also stay silent, but here it is decided on the
        // exact surface, not the tessellation.
        var solid = SolidFactory.MakeSphere(3);
        Assert.Empty(BrepFeatureEdges.Extract(solid));
    }

    [Fact]
    public void FinerSamplingThanTheMesh_IsTheWholePoint()
    {
        // The same solid tessellated coarsely still yields display-resolution edges:
        // extraction reads the exact curves, so a 12-segment mesh circle does not
        // limit the overlay's 96-segment rims.
        var solid = SolidFactory.MakeCylinder(1, 2);
        var coarseMesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 12);
        var edges = BrepFeatureEdges.Extract(solid, segmentsPerCircle: 96);
        Assert.True(coarseMesh.FaceCount < 100);   // genuinely coarse
        Assert.Equal(2 * 96, edges.Count);
    }
}
