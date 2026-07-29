using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Variable-RADIUS fillets: the law is evaluated at rim corners and interpolates linearly
/// along each edge. Geometry and refusals here; tessellated volumes in Interop.Tests.
/// </summary>
public class VariableFilletTests
{
    /// <summary>
    /// A rounded-rectangle plate: four straight runs joined tangentially by four quarter
    /// arcs. Tangent continuity throughout is what makes a VARYING law legal on the straights
    /// — every corner is smooth, so no miter is asked for.
    /// </summary>
    private static BrepSolid RoundedPlate(double width, double depth, double corner, double height)
    {
        double halfWidth = width / 2, halfDepth = depth / 2;
        double x = halfWidth - corner, y = halfDepth - corner;
        Vector3d P(double a, double b) => new(a, b, 0);
        // CurveSegment over Circle3d: the spelling whose Underlying IS a circle, which is what
        // the rim surgery recognizes as an arc edge.
        Curve3d Arc(double cx, double cy, double from) =>
            new CurveSegment(
                new Circle3d(P(cx, cy), Vector3d.UnitX, Vector3d.UnitY, corner), from, from + Math.PI / 2);

        var curves = new List<Curve3d>
        {
            new Line3d(P(-x, -halfDepth), P(x, -halfDepth)),
            Arc(x, -y, -Math.PI / 2),
            new Line3d(P(halfWidth, -y), P(halfWidth, y)),
            Arc(x, y, 0),
            new Line3d(P(x, halfDepth), P(-x, halfDepth)),
            Arc(-x, y, Math.PI / 2),
            new Line3d(P(-halfWidth, y), P(-halfWidth, -y)),
            Arc(-x, -y, Math.PI),
        };
        return SolidFactory.Extrude(new Profile(curves), Vector3d.UnitZ * height);
    }

    private static BrepFace TopOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    /// <summary>
    /// A radius running 2 → 3.5 along the plate's two long sides and CONSTANT over the corner
    /// arcs (it saturates outside |x| &lt; 22, which is exactly the straight runs' extent).
    /// That shape is not a convenience: an arc takes the law only where it is constant along
    /// the arc, so a law that varies there is refused rather than approximated.
    /// </summary>
    private static double TaperedLaw(Vector3d p) =>
        2 + 1.5 * Math.Clamp((p.X + 22) / 44, 0, 1);

    [Fact]
    public void VariableRadiusOnATangentRim_BuildsRuledSkinBands()
    {
        // A rounded plate: the arcs keep a constant radius (a circle offset by a varying
        // amount is a spiral) and the straight runs between them interpolate.
        var plate = RoundedPlate(60, 40, 8, 10);
        var top = TopOf(plate);
        var filleted = Filleting.FilletRim(plate, top, TaperedLaw);
        filleted.Validate();

        // At least one band came back as a ruled skin: that is the variable band.
        Assert.Contains(filleted.Faces, f => f.Surface is LoftedSurface);
        // ... and the constant-radius arc bands are still exact tori.
        Assert.Contains(filleted.Faces, f => f.Surface is RevolvedSurface);
    }

    [Fact]
    public void EveryCrossSectionOfAVariableBand_IsATrueCircleOfTheLerpedRadius()
    {
        // The exactness claim, checked rather than asserted: because the two end arcs are
        // equal-weight rational conics on one frame, lerping their points equals lerping
        // their homogeneous control points, so an intermediate section is a TRUE circle.
        var plate = RoundedPlate(60, 40, 8, 10);
        var filleted = Filleting.FilletRim(plate, TopOf(plate), TaperedLaw);
        var skin = (LoftedSurface)filleted.Faces.First(f => f.Surface is LoftedSurface).Surface;

        double r0 = Radius(skin, 0);
        double r1 = Radius(skin, 1);
        for (int i = 1; i < 8; i++)
        {
            double v = i / 8.0;
            Assert.Equal(r0 + (r1 - r0) * v, Radius(skin, v), 9);
        }
    }

    [Fact]
    public void AVariableBandStaysTangentToBothNeighbours()
    {
        // The whole point of a fillet: G1 with the flat face along its top boundary and with
        // the side wall along its bottom. Both hold at every station, not just the ends.
        var plate = RoundedPlate(60, 40, 8, 10);
        var filleted = Filleting.FilletRim(plate, TopOf(plate), TaperedLaw);
        var skin = (LoftedSurface)filleted.Faces.First(f => f.Surface is LoftedSurface).Surface;

        for (int i = 0; i <= 8; i++)
        {
            double v = i / 8.0;
            // u = 0 is the top tangency: the normal there must be the flat face's (+Z).
            Assert.Equal(1, skin.NormalAt(skin.DomainU.Start, v).Dot(Vector3d.UnitZ), 6);
            // u = 1 is the side tangency: the normal must be horizontal.
            Assert.Equal(0, skin.NormalAt(skin.DomainU.End, v).Dot(Vector3d.UnitZ), 6);
        }
    }

    [Fact]
    public void AConstantLawIsBitIdenticalToThePlainRadiusOverload()
    {
        // The law path must not perturb the case it generalizes.
        var plate = RoundedPlate(60, 40, 8, 10);
        var uniform = Filleting.FilletRim(plate, TopOf(plate), 3);
        var second = RoundedPlate(60, 40, 8, 10);
        var law = Filleting.FilletRim(second, TopOf(second), _ => 3.0);
        Assert.Equal(uniform.Faces.Count(), law.Faces.Count());
        Assert.Equal(
            uniform.Faces.Select(f => f.Surface.GetType().Name).OrderBy(s => s),
            law.Faces.Select(f => f.Surface.GetType().Name).OrderBy(s => s));
        var uniformPoints = uniform.Vertices.Select(v => v.Position).OrderBy(p => p.X)
            .ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var lawPoints = law.Vertices.Select(v => v.Position).OrderBy(p => p.X)
            .ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        Assert.Equal(uniformPoints.Count, lawPoints.Count);
        for (int i = 0; i < uniformPoints.Count; i++)
            Assert.Equal(uniformPoints[i], lawPoints[i]); // exact equality, not a tolerance
    }

    // ---- refusals, by name ----

    [Fact]
    public void AVaryingLawAcrossASharpCorner_IsRefusedByName()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (30, 20, 10)));
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletRim(box, TopOf(box), p => 2 + 0.05 * p.X));
        Assert.Contains("circumscribe a common sphere", exception.Message);
        Assert.Contains("quartic", exception.Message);
    }

    [Fact]
    public void AConstantLawAcrossSharpCorners_StillMitersExactly()
    {
        // The band is a cylinder again, so the bicylinder ellipse is back and the corner is
        // exact — which is why the refusal is about the LAW and not about sharp corners.
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (30, 20, 10)));
        var filleted = Filleting.FilletRim(box, TopOf(box), _ => 2.0);
        filleted.Validate();
        Assert.Contains(filleted.Faces, f => f.Surface is ExtrudedSurface);
        Assert.DoesNotContain(filleted.Faces, f => f.Surface is LoftedSurface);
    }

    [Fact]
    public void AVaryingLawAlongAnArc_IsRefusedByName()
    {
        // A circle offset by a varying amount is a spiral: the chamfer precedent, and the
        // same reason.
        var plate = RoundedPlate(60, 40, 8, 10);
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletRim(plate, TopOf(plate), p => 2 + 0.05 * p.Y));
        Assert.Contains("spiral", exception.Message);
    }

    [Fact]
    public void AVaryingLawOnAFullCircularRim_IsRefusedByName()
    {
        var cylinder = SolidFactory.MakeCylinder(10, 20);
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletRim(cylinder, TopOf(cylinder), p => 2 + 0.05 * p.X));
        Assert.Contains("spiral", exception.Message);
    }

    private static double Radius(LoftedSurface skin, double v)
    {
        // Circumradius of three samples of the v-section: a true circle makes all three
        // agree with every other triple, which the caller's lerp assertion then pins.
        var a = skin.PointAt(skin.DomainU.ParameterAt(0), v);
        var b = skin.PointAt(skin.DomainU.ParameterAt(0.5), v);
        var c = skin.PointAt(skin.DomainU.ParameterAt(1), v);
        var u = b - a;
        var w = c - a;
        var plane = u.Cross(w);
        var centre = a + (plane.Cross(u) * w.LengthSquared + w.Cross(plane) * u.LengthSquared)
            / (2 * plane.LengthSquared);
        double radius = centre.DistanceTo(a);
        // Every other sample must sit on that circle, or the section is not one.
        for (int i = 1; i < 8; i++)
        {
            var point = skin.PointAt(skin.DomainU.ParameterAt(i / 8.0), v);
            Assert.Equal(radius, centre.DistanceTo(point), 9);
        }
        return radius;
    }
}
