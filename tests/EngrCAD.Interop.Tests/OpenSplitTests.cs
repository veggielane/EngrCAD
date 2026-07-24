using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Open-curve face splitting verified through whole solids: splitting a face patches its
/// neighbors through their shared edges, so the rebuilt solid must stay manifold, satisfy
/// Euler–Poincaré, and keep its exact volume.
/// </summary>
public class OpenSplitTests
{
    private static readonly Aabb Region = new((-3, -3, -3), (5, 5, 5));

    private static BrepSolid Rebuild(BrepSolid solid, IEnumerable<BrepFace> remove, IEnumerable<BrepFace> add)
    {
        var faces = solid.Faces.Where(f => !remove.Contains(f)).Concat(add).ToList();
        return new BrepSolid([new BrepShell(faces)]);
    }

    private static BrepFace TopFace(BrepSolid solid) =>
        solid.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));

    [Fact]
    public void SplitBoxTop_ByOneLine_TwoFacesExactVolume()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var top = TopFace(box);
        var cutter = new PlaneSurface((0.7, 0, 0), Vector3d.UnitY, Vector3d.UnitZ); // x = 0.7
        var line = Assert.Single(SurfaceIntersection.Intersect(top.Surface, cutter, Region));

        var parts = FaceSplitter.SplitByCurve(top, line);
        Assert.Equal(2, parts.Count);

        // The two sides classify probe points oppositely.
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.3, 1, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.5, 1, 2)));

        var solid = Rebuild(box, [top], parts);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(7, solid.Faces.Count());

        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(8.0, mesh.Volume(), 9);
    }

    [Fact]
    public void CrossSplit_FourFaces_ThroughSharedVertex()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var top = TopFace(box);
        var lineX = Assert.Single(SurfaceIntersection.Intersect(
            top.Surface, new PlaneSurface((0.7, 0, 0), Vector3d.UnitY, Vector3d.UnitZ), Region));
        var lineY = Assert.Single(SurfaceIntersection.Intersect(
            top.Surface, new PlaneSurface((0, 1.3, 0), Vector3d.UnitZ, Vector3d.UnitX), Region));

        var firstSplit = FaceSplitter.SplitByCurve(top, lineX);
        Assert.Equal(2, firstSplit.Count);

        // The second line crosses both sub-faces — and passes exactly through the vertex
        // the first split created on their shared edge.
        var parts = firstSplit.SelectMany(f => FaceSplitter.SplitByCurve(f, lineY)).ToList();
        Assert.Equal(4, parts.Count);

        var solid = Rebuild(box, [top], parts); // firstSplit faces were intermediate only
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(9, solid.Faces.Count());

        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(8.0, mesh.Volume(), 9);

        // One part per quadrant.
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.3, 0.5, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.5, 0.5, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (0.3, 1.8, 2)));
        Assert.Single(parts, f => FaceGeometry.Contains(f, (1.5, 1.8, 2)));
    }

    [Fact]
    public void SplitAnnularCap_LineBesideHole_HoleFollowsItsSide()
    {
        int n = 32;
        double radius = 0.5;
        var plate = SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0)]),
            (0, 0, 1),
            holes: [Profile.Circle((1, 1, 0), Vector3d.UnitX, Vector3d.UnitY, radius)]);
        var top = plate.Faces.First(f =>
            f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default) && f.Loops.Count == 2);

        // A line beside the hole: the hole loop is uncrossed, traces as a CW loop, and
        // must be assigned to the sub-face containing it. (Cutting *through* the hole
        // is exercised by TrimmedFaceTests.Difference_SlotThroughBore_*.)
        var cutter = new PlaneSurface((0.3, 0, 0), Vector3d.UnitY, Vector3d.UnitZ); // x = 0.3
        var line = Assert.Single(SurfaceIntersection.Intersect(top.Surface, cutter, Region));
        var parts = FaceSplitter.SplitByCurve(top, line);
        Assert.Equal(2, parts.Count);
        var left = Assert.Single(parts, f => FaceGeometry.Contains(f, (0.1, 1, 1)));
        var right = Assert.Single(parts, f => FaceGeometry.Contains(f, (1.8, 1, 1)));
        Assert.Single(left.Loops);
        Assert.Equal(2, right.Loops.Count); // the hole followed the right side
        Assert.False(FaceGeometry.Contains(right, (1, 1, 1))); // inside the hole

        var solid = Rebuild(plate, [top], parts);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));

        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: n);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // still genus 1

        double holeArea = 0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n);
        Assert.Equal(4.0 - holeArea, mesh.Volume(), 9);
    }

    [Fact]
    public void CurveMissingTheFace_ReturnsFaceUnchanged()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var top = TopFace(box);
        var line = new Line3d((5, 0, 2), (5, 2, 2));
        var parts = FaceSplitter.SplitByCurve(top, line);
        Assert.Same(top, Assert.Single(parts));
    }

    [Fact]
    public void OpenCurveEndingInsideFace_IsRejected()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var top = TopFace(box);
        var dangling = new Line3d((-1, 1, 2), (1, 1, 2)); // ends mid-face
        Assert.Throws<NotSupportedException>(() => FaceSplitter.SplitByCurve(top, dangling));
    }
}
