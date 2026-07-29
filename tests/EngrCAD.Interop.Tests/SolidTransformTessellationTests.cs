using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Posed solids through the two consumers that decide whether a re-placement really produced
/// a usable solid: the tessellator and the boolean.
///
/// <para>These are the tests that make <see cref="BrepSolid.Transformed"/> more than a
/// structural claim. `Validate()` and an Euler count pass on a solid whose surfaces have
/// drifted out of their own trim domains, or whose grid samples no longer land on its edges;
/// what catches that is meshing it and asking for the volume, and then feeding it to a
/// boolean, which consumes the topology and re-splits the faces.</para>
/// </summary>
public class SolidTransformTessellationTests
{
    /// <summary>A rotation about a skew axis plus an offset — nothing axis-aligned, so no
    /// surface can pass by accidentally commuting with the map.</summary>
    private static Matrix4d Pose() =>
        Matrix4d.CreateTranslation((13, -7, 4))
        * Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7);

    [Fact]
    public void APosedBoxTessellatesClosedWithTheSameVolume()
    {
        var moved = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))).Transformed(Pose());
        var mesh = BRepTessellator.Tessellate(moved);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(20.0 * 14 * 8, mesh.Volume(), 9);
    }

    [Fact]
    public void APosedCylinderTessellatesToTheSameDISCRETEVolume()
    {
        // Compared against the untransformed tessellation rather than against pi*r^2*h: an
        // isometry preserves the inscribed n-gon exactly, so the two meshes must agree to
        // round-off, where the analytic value would only agree to the chord error and so
        // could not tell a faithful pose from a slightly wrong one.
        var cylinder = SolidFactory.MakeCylinder(5, 12);
        var here = BRepTessellator.Tessellate(cylinder);
        var moved = BRepTessellator.Tessellate(SolidFactory.MakeCylinder(5, 12).Transformed(Pose()));

        moved.Validate();
        Assert.True(moved.IsClosed);
        Assert.Equal(here.FaceCount, moved.FaceCount);
        Assert.Equal(here.Volume(), moved.Volume(), 9);
    }

    [Fact]
    public void APosedBooleanResultStillTessellatesAndStillBooleans()
    {
        // The hard case: a drilled plate's bore wall is a trimmed face whose loops came out
        // of face splitting, and its rim may be a traced polyline. Posing it must move the
        // trim with the surface — and the result must survive being consumed by a SECOND
        // boolean, which is what proves the graph is complete and independently owned.
        var plate = BrepBoolean.Difference(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 10))),
            SolidFactory.MakeCylinder(4, 40).Transformed(Matrix4d.CreateTranslation((20, 15, -15))));

        var moved = plate.Transformed(Pose());
        moved.Validate();
        var mesh = BRepTessellator.Tessellate(moved);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic);          // genus 1: one through bore

        // The pose is faithful: same discrete volume as the un-posed plate.
        var here = BRepTessellator.Tessellate(plate);
        Assert.Equal(here.Volume(), mesh.Volume(), 9);

        // And it is still boolean-able — a notch off one corner, in the posed frame.
        var notch = SolidFactory.MakeBox(new Aabb((-1, -1, -1), (6, 6, 12))).Transformed(Pose());
        var cut = BrepBoolean.Difference(moved, notch);
        cut.Validate();
        var cutMesh = BRepTessellator.Tessellate(cut);
        cutMesh.Validate();
        Assert.True(cutMesh.IsClosed);
        Assert.Equal(mesh.Volume() - 6.0 * 6 * 10, cutMesh.Volume(), 6);
    }

    [Fact]
    public void APosedSphereKeepsItsPolesAndItsVolume()
    {
        // A sphere is the one surface with no frame of its own — its u/v are measured
        // against the WORLD axes — so posing RE-PARAMETERIZES it. That is sound only
        // because trim loops are pulled back from 3D edge curves at use rather than stored
        // in uv, and this is the test that says so.
        var sphere = SolidFactory.MakeSphere(7);
        var here = BRepTessellator.Tessellate(sphere);
        var moved = BRepTessellator.Tessellate(sphere.Transformed(Pose()));

        moved.Validate();
        Assert.True(moved.IsClosed);
        Assert.Equal(here.Volume(), moved.Volume(), 9);
    }
}
