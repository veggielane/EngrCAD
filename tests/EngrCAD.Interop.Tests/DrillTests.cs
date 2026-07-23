using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// End-to-end rehearsal of the B-Rep boolean pipeline on the drill case: surface–surface
/// intersection finds the circles, face splitting cuts the caps, and a bore band stitches
/// the hole — hand-orchestrated here, to be automated by BrepBoolean later.
/// </summary>
public class DrillTests
{
    [Fact]
    public void DrillHoleThroughBox_ViaIntersectionAndSplitting()
    {
        int n = 48;
        double radius = 0.4;
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var bore = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, radius);
        var region = new Aabb((-2, -2, -1), (2, 2, 2));

        var top = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
        var bottom = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(-Vector3d.UnitZ, Tolerance.Default));
        var sides = box.Faces.Where(f => !ReferenceEquals(f, top) && !ReferenceEquals(f, bottom)).ToList();

        // 1. Where does the bore meet the caps?
        var topCircle = Assert.IsType<Circle3d>(Assert.Single(SurfaceIntersection.Intersect(top.Surface, bore, region)));
        var bottomCircle = Assert.IsType<Circle3d>(Assert.Single(SurfaceIntersection.Intersect(bottom.Surface, bore, region)));

        // 2. Split the caps; the disks are the drilled-away material, so their edge use
        //    goes to the bore band instead.
        var topSplit = FaceSplitter.SplitByClosedCurve(top, topCircle, createDisk: false);
        var bottomSplit = FaceSplitter.SplitByClosedCurve(bottom, bottomCircle, createDisk: false);

        // 3. The bore wall. Intersection circles are phase-aligned with the cylinder's
        //    frame and wind counter-clockwise about +Z, so the generator must be the
        //    reversed bottom circle for the wall's normal to face the hole — out of the
        //    material. The bottom loop follows the generator (against the edge's curve),
        //    the top loop opposes it.
        var wall = new BrepFace(
            new ExtrudedSurface(bottomCircle.Reversed(), (0, 0, 1)),
            [
                new BrepLoop([new BrepCoedge(bottomSplit.Edge, sameSense: false)]),
                new BrepLoop([new BrepCoedge(topSplit.Edge, sameSense: true)]),
            ]);

        var drilled = new BrepSolid([new BrepShell(
            [.. sides, topSplit.FaceWithHole, bottomSplit.FaceWithHole, wall])]);

        drilled.Validate();
        Assert.True(drilled.SatisfiesEulerFormula(genus: 1));

        // 4. Tessellate and measure: the bore is an n-gon prism, so the volume is exact.
        var mesh = BRepTessellator.Tessellate(drilled, segmentsPerCircle: n);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1

        double boreArea = 0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n);
        Assert.Equal(4.0 - boreArea, mesh.Volume(), 9);
    }
}
