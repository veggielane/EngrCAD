using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The edges of what direct editing reaches, pinned so they are known boundaries rather
/// than surprises. Both were found by a DOCS RENDER rather than by a unit test, which is
/// the argument for the docs being executable.
/// </summary>
public class DirectEditScopeTests
{
    [Fact]
    public void OffsettingACurvedFaceOfBooleanOutput_Works()
    {
        // A difference marks the subtracted tool's walls IsReversed. CarrierBody now offsets a
        // reversed carrier by -distance (its OUTWARD normal is the negative of its surface's),
        // so the bore a boolean cut can be pushed like any other face — the input direct editing
        // exists for, boolean output having no history. A -3 offset moves the bore wall along its
        // outward normal (which points into the void), enlarging the Ø18 bore to Ø24.
        var housing = Shape.Cylinder(20, 30) - Shape.Cylinder(9, 40);
        var edited = housing.OffsetFaces(-3, FaceSetRef.Cylindrical(9));

        var solid = edited.ToBrep();
        solid.Validate();

        // The bore wall is still a reversed cylinder, now at radius 12, and the outer wall
        // (r20) did not move. Cylindrical faces are located by their radius, not by a stored
        // plane origin, so the Bounds().Center trap does not apply here.
        Assert.Contains(solid.Faces, f =>
            f.IsReversed && f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 12) < 1e-6);
        Assert.Contains(solid.Faces, f =>
            f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 20) < 1e-6);
    }

    [Fact]
    public void OffsettingACurvedFaceOfAPRIMITIVE_Works()
    {
        // The same operation on geometry no boolean has touched: a cone's slant face pushed
        // outward stays a cone, which is what SurfaceOffset's same-family rule promises.
        var cone = Shape.Cone(20, 12, 30);
        var fattened = cone.OffsetFaces(3, FaceSetRef.Where("slant", f => !f.IsPlanar(out _, out _)));

        var solid = fattened.ToBrep();
        solid.Validate();
        Assert.Equal(3, solid.Faces.Count());

        // Both rims grew: the offset of a cone is a cone, moved along its own normal.
        var radii = solid.Vertices
            .Select(v => Math.Sqrt(v.Position.X * v.Position.X + v.Position.Y * v.Position.Y))
            .OrderBy(r => r).ToList();
        Assert.True(radii[0] > 12, $"top rim should have grown past 12, got {radii[0]:F3}");
        Assert.True(radii[^1] > 20, $"bottom rim should have grown past 20, got {radii[^1]:F3}");
    }

    [Fact]
    public void AnEditedPlate_TakesHolesAndARimChamferAfterwards()
    {
        // The composition the docs page shows: direct editing hands an ordinary solid back,
        // so the feature vocabulary keeps working on it.
        var part = Shape.Box(60, 40, 10)
            .OffsetFaces(6, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ))
            .Drill(StandardHoles.Clearance(6), [new(-20, 0), new(20, 0)], depth: 16)
            .Chamfer(1.5, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ));

        var solid = part.ToBrep();
        solid.Validate();

        // Thickened from 10 to 16, and still drilled right through.
        var bounds = Aabb.Empty;
        foreach (var vertex in solid.Vertices)
            bounds = bounds.Union(vertex.Position);
        Assert.Equal(-5, bounds.Min.Z, 6);
        Assert.Equal(11, bounds.Max.Z, 6);
        // Both bores survived the chamfer.
        Assert.Equal(2, solid.Faces.Count(f => f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 3.3) < 1e-6));
    }
}
